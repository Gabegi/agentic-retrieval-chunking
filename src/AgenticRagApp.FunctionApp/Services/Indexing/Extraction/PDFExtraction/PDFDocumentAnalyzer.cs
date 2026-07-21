using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.RegularExpressions;
using Azure;
using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.DocumentIntelligence;
using AgenticRagApp.Models;
using AgenticRagApp.Observability;

namespace AgenticRagApp.Services
{
    // Handles everything Document Intelligence (DI) needs for one PDF, except preflight
    // checks and PdfPig-native reads:
    // - Makes the one paid analyze call, polling for completion itself and retrying only
    //   the status poll on 429 - never resubmitting the paid request.
    // - Validates the response (markdown format, non-empty) before trusting any offset in it.
    // - Extracts every DI structural feature (headings, boilerplate, tables, page
    //   dimensions, selection marks, figures, sections, page quality, lines) into
    //   PdfDocumentStructure.
    // - Surfaces DI's own warnings plus whatever this class flags along the way.
    public sealed class PdfDocumentAnalyzer
    {
        // Azure "prebuilt-layout" pricing: $10 / 1,000 pages = $0.01/page (at time of
        // writing). Verify current pricing before trusting any cost estimate based on this.
        private const decimal CostPerPage = 0.01m;

        // Backoff schedule after a 429, Microsoft's recommended values.
        private static readonly TimeSpan[] BackoffDelays =
        {
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(13), TimeSpan.FromSeconds(34)
        };

        // Ordinary (non-throttled) interval between status-poll GETs - unrelated to
        // BackoffDelays, which only applies after a 429.
        private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(2);

        private readonly IDocumentAnalysisClient _diClient;
        private readonly ILogger _logger;

        public PdfDocumentAnalyzer(IDocumentAnalysisClient diClient, ILogger<PdfDocumentAnalyzer> logger)
        {
            _diClient = diClient;
            _logger = logger;
        }

        // Main entry point - called after preflight/native-metadata have already run.
        // Expects the caller to have already:
        // - Validated the PDF (PdfDocumentValidator.IsPDFValid).
        // - Read nativeMetadata/bookmarks (PdfNativeMetadataExtractor) and closed the
        //   PdfDocument - this method only ever sees the resulting data, never the
        //   PdfDocument itself.
        // Steps:
        // 1. Submit to DI and validate the response (DIAnalyzeDocumentAsync).
        // 2. On failure, return Ok=false with a typed ExtractionError.
        // 3. On success, build pages/title and extract every structural feature into
        //    PdfDocumentStructure.
        public async Task<PDFStructureExtractorResult> AnalyzeDocumentAsync(
            byte[] pdfBytes, string blobName, DocMetadata nativeMetadata, CancellationToken ct = default)
        {
            var analyzeResults = await DIAnalyzeDocumentAsync(pdfBytes, blobName, ct);

            if (!TryValidateAnalyzeOutcome(analyzeResults, blobName, out var analysis, out var error))
                return new PDFStructureExtractorResult(false, null, null, null, null, error);

            // pages.Count reused below for EstimatedCostUsd - same count as analysis.Pages,
            // but already non-null, so no null-forgiving operator needed.
            var (pages, pageWarnings)       = GetPages(analysis, blobName, GetTitle(nativeMetadata, blobName));
            var (pageQuality, qualityWarnings) = GetPageQuality(analysis, blobName);
            var tables      = GetTables(analysis);
            var figures     = GetFigures(analysis);
            var structureWarnings = StructureWarnings(tables, figures, pages.Count, blobName);

            return new PDFStructureExtractorResult(
                true,
                analysis.Content,
                pages,
                new PdfDocumentStructure(
                GetHeadings(analysis),
                GetBoilerplate(analysis),
                tables,
                GetPageDimensions(analysis),
                GetSelectionMarks(analysis),
                figures,
                GetLines(analysis),
                GetSections(analysis),
                pageQuality),
                pages.Count * CostPerPage, null)
            {
                // Merges warnings from every stage (DI's own, non-BMP check, per-page
                // table-balance check, page quality, figure/table/cost summary) into one
                // list, regardless of which stage found them.
                Warnings = analyzeResults.Warnings.Concat(GetWarnings(analysis)).Concat(pageWarnings)
                    .Concat(qualityWarnings).Concat(structureWarnings).ToList(),
            };
        }

        // File-level structural summary findings: figures/tables DI extracted, but that
        // came back incomplete or malformed, plus a cost echo - all computed from data
        // GetTables/GetFigures already produced, so flagged here at the source rather than
        // recomputed by PdfPipelineValidator later.
        // internal (not private): unit tests build a real AnalyzeResult via
        // ModelReaderWriter.Read<AnalyzeResult>(json) and call this directly, bypassing the
        // live Document Intelligence call GetPages/GetTables/GetFigures don't need.
        internal static IReadOnlyList<AnalysisWarning> StructureWarnings(
            IReadOnlyList<TableInfo> tables, IReadOnlyList<FigureInfo> figures, int pageCount, string blobName)
        {
            var warnings = new List<AnalysisWarning>();

            var uncaptionedFigures = figures.Count(f => f.Caption is null);
            if (uncaptionedFigures > 0)
                warnings.Add(new AnalysisWarning(
                    "FiguresWithoutCaption",
                    $"{uncaptionedFigures} of {figures.Count} figure(s) have no caption.",
                    blobName));

            var malformedTables = tables.Count(t => t.Cells.Count == 0 || t.RowCount == 0 || t.ColumnCount == 0);
            if (malformedTables > 0)
                warnings.Add(new AnalysisWarning(
                    "MalformedTable",
                    $"{malformedTables} of {tables.Count} table(s) have no cells or a zero row/column count.",
                    blobName));

            var estimatedCost = pageCount * CostPerPage;
            warnings.Add(new AnalysisWarning(
                "EstimatedCost",
                $"Estimated cost: ${estimatedCost:F2} ({pageCount} page(s) at ${CostPerPage}/page).",
                blobName));

            return warnings;
        }

        // Submits the PDF to DI exactly once, then polls for completion itself instead of
        // letting the SDK do it via WaitUntil.Completed:
        // 1. Submit (WaitUntil.Started) - returns as soon as the POST succeeds; the
        //    analysis is now running server-side, already paid for.
        // 2. If submission itself fails, return Ok=false immediately.
        // 3. Poll operation.UpdateStatusAsync in a loop until HasCompleted:
        //    - On 429, back off using BackoffDelays and retry the SAME poll - never
        //      resubmits the paid analysis.
        //    - Any other failure, or backoff exhausted, returns Ok=false with a typed reason.
        //    - Otherwise wait PollingInterval and poll again.
        // 4. Once complete, hand the result to ValidateAnalyzeResult.
        //
        // Why WaitUntil.Started matters: a known SDK bug (Azure/azure-sdk-for-net#50904)
        // means DI's own internal polling (used by WaitUntil.Completed) can still hit 429
        // even with client-level retry configured. Retrying that by resubmitting from
        // scratch would pay for a brand-new analysis every time (up to 5x cost under
        // sustained throttling). Polling manually here means a 429 only ever retries the
        // free status-poll GET, never the paid POST.
        //
        // OperationCanceledException is the one exception that still propagates instead of
        // becoming a typed error: it means ct fired (e.g. host shutdown), not a
        // per-document failure. A zero-page result also becomes Ok=false (see
        // ValidateAnalyzeResult): preflight already rejects zero-page PDFs, so DI
        // returning zero pages here means the analysis itself failed, not a
        // successful-but-empty result.
        private async Task<AnalyzeOutcome> DIAnalyzeDocumentAsync(byte[] pdfBytes, string blobName, CancellationToken ct)
        {
            _logger.LogInformation("Submitting '{Blob}' to Document Intelligence.", blobName);

            // Operational health (throttling, wall-clock time), not per-file content
            // quality - emitted as OTel metrics via Instrumentation, not as a
            // per-file AnalysisWarning. See indexer.di_throttle_retries/
            // indexer.di_analyze_duration_seconds.
            var sw            = Stopwatch.StartNew();
            var throttleCount = 0;

            // Markdown output (vs. DI's default plain text): renders tables as HTML
            // <table> elements with real rowspan/colspan (GetTables relies on this) and
            // keeps RawContent in the format downstream chunking is meant to consume.
            var analyzeOptions = new AnalyzeDocumentOptions("prebuilt-layout", BinaryData.FromBytes(pdfBytes))
            {
                OutputContentFormat = DocumentContentFormat.Markdown,
            };

            try
            {
                return await SubmitAndPollAsync(pdfBytes, blobName, analyzeOptions, ct, throttleCounter => throttleCount = throttleCounter);
            }
            finally
            {
                // Recorded regardless of outcome (success, DI failure, or cancellation) -
                // operational health applies whether or not the analysis itself succeeded.
                Instrumentation.DiAnalyzeDuration.Record(sw.Elapsed.TotalSeconds);
                if (throttleCount > 0)
                    Instrumentation.DiThrottleRetries.Add(throttleCount);
            }
        }

        private async Task<AnalyzeOutcome> SubmitAndPollAsync(
            byte[] pdfBytes, string blobName, AnalyzeDocumentOptions analyzeOptions, CancellationToken ct,
            Action<int> reportThrottleCount)
        {
            Operation<AnalyzeResult> operation;
            try
            {
                operation = await _diClient.SubmitAnalyzeAsync(analyzeOptions, ct);
            }
            catch (RequestFailedException ex)
            {
                _logger.LogWarning(ex, "Document Intelligence rejected the analyze submission for '{Blob}'.", blobName);
                return new AnalyzeOutcome(false, null, new ExtractionError
                {
                    DocumentId = blobName,
                    Message = $"Document Intelligence request failed ({ex.Status}): {ex.Message}",
                    Reason = ex.Status == 429 ? PdfOpenFailureReason.Throttled : PdfOpenFailureReason.DiServiceError,
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Unexpected error submitting '{Blob}' to Document Intelligence.", blobName);
                return new AnalyzeOutcome(false, null, new ExtractionError
                {
                    DocumentId = blobName,
                    Message = $"Document Intelligence analysis failed unexpectedly: {ex.Message}",
                    Reason = PdfOpenFailureReason.Unknown,
                });
            }

            var throttleCount = 0;

            for (var attempt = 0; !operation.HasCompleted; attempt++)
            {
                try
                {
                    await operation.UpdateStatusAsync(ct);
                }
                catch (RequestFailedException ex) when (ex.Status == 429 && attempt < BackoffDelays.Length)
                {
                    throttleCount++;
                    reportThrottleCount(throttleCount);

                    var wait = BackoffDelays[attempt];
                    _logger.LogWarning("DI throttled polling '{Blob}' (attempt {Attempt}); backing off {Wait}.", blobName, attempt + 1, wait);
                    await Task.Delay(wait, ct);
                    continue;
                }
                catch (RequestFailedException ex)
                {
                    if (ex.Status == 429)
                        _logger.LogWarning(ex, "Document Intelligence throttled '{Blob}' - retries exhausted after {Attempts} attempt(s).", blobName, attempt + 1);
                    else
                        _logger.LogWarning(ex, "Document Intelligence failed while polling '{Blob}'.", blobName);

                    return new AnalyzeOutcome(false, null, new ExtractionError
                    {
                        DocumentId = blobName,
                        Message = $"Document Intelligence request failed ({ex.Status}): {ex.Message}",
                        Reason = ex.Status == 429 ? PdfOpenFailureReason.Throttled : PdfOpenFailureReason.DiServiceError,
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Unexpected error polling '{Blob}' with Document Intelligence.", blobName);
                    return new AnalyzeOutcome(false, null, new ExtractionError
                    {
                        DocumentId = blobName,
                        Message = $"Document Intelligence analysis failed unexpectedly: {ex.Message}",
                        Reason = PdfOpenFailureReason.Unknown,
                    });
                }

                if (!operation.HasCompleted)
                    await Task.Delay(PollingInterval, ct);
            }

            return ValidateAnalyzeResult(operation.Value, blobName);
        }

        // Decides whether a raw DI response is usable - checks run cheapest/most
        // fundamental first, so a bad response fails fast:
        // 1. Content format must be Markdown, or fail (ValidateContentFormat).
        // 2. Check for non-BMP characters - diagnostic only, never fails.
        // 3. Must have at least one page, or fail.
        // Kept separate from DIAnalyzeDocumentAsync's retry loop, which only needs to know
        // "call succeeded, now validate" - not each individual rule.
        private AnalyzeOutcome ValidateAnalyzeResult(AnalyzeResult result, string blobName)
        {
            var formatError = ValidateContentFormat(result, blobName);
            if (formatError != null)
            {
                _logger.LogWarning(
                    "Document Intelligence returned unexpected content format '{Format}' for '{Blob}'.",
                    result.ContentFormat, blobName);
                return new AnalyzeOutcome(false, null, formatError);
            }

            var warnings = CheckNonBmpCharacters(result, blobName);

            if ((result.Pages?.Count ?? 0) == 0)
            {
                _logger.LogWarning("Document Intelligence returned zero pages for '{Blob}'.", blobName);
                return new AnalyzeOutcome(false, null, new ExtractionError
                {
                    DocumentId = blobName,
                    Message = "Document Intelligence analysis returned zero pages.",
                    Reason = PdfOpenFailureReason.EmptyDocument,
                });
            }

            return new AnalyzeOutcome(true, result, null) { Warnings = warnings };
        }

        // Folds two failure signals into the one question callers actually have - "do I
        // have a usable analysis?" - using the same Try(out, out) + [NotNullWhen] shape as
        // PdfDocumentValidator.IsPDFValid, so the compiler (not a comment) proves result
        // is non-null on true and error is non-null on false.
        // - Ok=false: use outcome.Error (already typed and logged by whichever check
        //   rejected it). If Error is somehow also null, that's a bug in this class - log
        //   it and synthesize a fallback error rather than let a null through.
        // - Ok=true but Result=null: DIAnalyzeDocumentAsync/ValidateAnalyzeResult are
        //   supposed to guarantee these travel together. Should never trip; if it does,
        //   log it (LogError, not the LogWarning used elsewhere - this is our own bug, not
        //   a DI-side failure) and return a typed error instead of a NullReferenceException
        //   surfacing downstream.
        private bool TryValidateAnalyzeOutcome(
            AnalyzeOutcome outcome, string blobName,
            [NotNullWhen(true)]  out AnalyzeResult?   result,
            [NotNullWhen(false)] out ExtractionError? error)
        {
            if (!outcome.Ok)
            {
                result = null;

                if (outcome.Error != null)
                {
                    error = outcome.Error;
                    return false;
                }

                _logger.LogError(
                    "'{Blob}': Document Intelligence analysis reported Ok=false but Error was null - this indicates a bug in DIAnalyzeDocumentAsync/ValidateAnalyzeResult.",
                    blobName);
                error = new ExtractionError
                {
                    DocumentId = blobName,
                    Message = "Document Intelligence analysis failed with no error details.",
                    Reason = PdfOpenFailureReason.Unknown,
                };
                return false;
            }

            if (outcome.Result != null)
            {
                result = outcome.Result;
                error  = null;
                return true;
            }

            _logger.LogError(
                "'{Blob}': Document Intelligence analysis reported Ok=true but returned no result - this indicates a bug in DIAnalyzeDocumentAsync/ValidateAnalyzeResult.",
                blobName);

            result = null;
            error = new ExtractionError
            {
                DocumentId = blobName,
                Message = "Document Intelligence analysis reported success but returned no result.",
                Reason = PdfOpenFailureReason.MissingAnalysisResult,
            };
            return false;
        }

        // Guards the trust boundary every Offset in this file depends on: offsets only
        // index correctly into analysis.Content if it's markdown (see OutputContentFormat
        // above). If DI ever returned Text instead, every offset would still "work" but
        // point at the wrong characters - failing loudly here is cheaper than debugging
        // garbled content several steps downstream later.
        private static ExtractionError? ValidateContentFormat(AnalyzeResult result, string blobName)
        {
            if (result.ContentFormat == DocumentContentFormat.Markdown) return null;

            return new ExtractionError
            {
                DocumentId = blobName,
                Message = $"Document Intelligence returned content format '{result.ContentFormat}', expected Markdown.",
                Reason = PdfOpenFailureReason.UnexpectedContentFormat,
            };
        }

        // Diagnostic only, never fails: flags characters needing a UTF-16 surrogate pair
        // (codepoints above the Basic Multilingual Plane - emoji, some math symbols; NOT
        // ordinary Dutch diacritics, which all fit in one UTF-16 unit).
        // - Why it matters: every Offset in this file assumes UTF-16 code-unit offsets,
        //   exactly what string.Substring expects. A surrogate pair doesn't prove that's
        //   broken here - it's just a signal worth a closer look if garbled content ever
        //   shows up downstream.
        // - Heuristic, not exact: a lone/unpaired surrogate contributes 0 (EnumerateRunes
        //   replaces it with a single U+FFFD rune), even though it's arguably more
        //   interesting than a well-formed pair. Only exact for well-formed strings.
        // - Logged immediately, and returned as an AnalysisWarning so it also reaches the
        //   blob-stored validation report.
        private IReadOnlyList<AnalysisWarning> CheckNonBmpCharacters(AnalyzeResult result, string blobName)
        {
            var nonBmpCount = result.Content.Length - result.Content.EnumerateRunes().Count();
            if (nonBmpCount <= 0) return [];

            _logger.LogWarning(
                "'{Blob}' contains {Count} non-BMP character(s) (UTF-16 surrogate pairs) in its analyzed content.",
                blobName, nonBmpCount);

            return [new AnalysisWarning(
                "NonBmpCharacters",
                $"Content contains {nonBmpCount} non-BMP character(s) (UTF-16 surrogate pairs).",
                null)];
        }

        // Title: nativeMetadata.Title if the PDF actually has one set, else derived from
        // the blob name.
        // - Path.GetFileNameWithoutExtension, not blobName.Split('/')[0]: for a nested
        //   path like "protocols/policy-2024.pdf", Split('/')[0] returns "protocols" (the
        //   folder), not the filename.
        private static string GetTitle(DocMetadata nativeMetadata, string blobName) =>
            !string.IsNullOrWhiteSpace(nativeMetadata.Title)
                ? nativeMetadata.Title
                : Path.GetFileNameWithoutExtension(blobName).Replace("-", " ");

        // One PdfPageRecord per page, sliced from analysis.Content by each page's own
        // Spans (DI's structural page model), not by splitting on "<!-- PageBreak -->" -
        // page boundaries come from DI's own per-page data, so no fragment-count mismatch
        // to guard against.
        // Steps per page:
        // 1. Slice content by Spans, strip DI's noise comments (PageHeader/Footer/Number/
        //    FigureContent - literal text DI repeats on every page they apply to), and
        //    normalize a setext title ("Title" + "===" underline) to ATX ("# Title") -
        //    cosmetic only, matches every other heading DI already renders as ATX.
        // 2. Warn if that leaves the page empty - shouldn't silently reach the index.
        // 3. Warn (don't repair) if <table>/</table> tags are unbalanced - a table split
        //    across pages will be caught by the chunk-builder's Sections-based boundaries
        //    later, so this is a "how often does it happen" signal, not a fix.
        // Both cleanups only ever run on PageContent, never on RawContent: they change
        // string length (setext especially), which would shift every offset after them if
        // applied to the offset-addressable source. See PdfPageRecord.PageContent.
        // Not ported from the deleted PDFMarkdownExtractor: heading carry-forward
        // (superseded by DI's own structural Headings/Sections) or page-level table
        // repair (see step 3). Bookmark breadcrumbs live in PDFSectionBreadCrumbBuilder,
        // for a future per-chunk (not per-page) use.
        // internal (not private): see StructureWarnings' note - testable without a live DI call.
        internal (IReadOnlyList<PdfPageRecord> Pages, IReadOnlyList<AnalysisWarning> Warnings) GetPages(
            AnalyzeResult result, string blobName, string title)
        {
            var pages    = new List<PdfPageRecord>();
            var warnings = new List<AnalysisWarning>();

            var setextNormalizedCount    = 0;
            var noiseCommentsStripped    = 0;
            var pagesWithNoiseComments   = 0;

            foreach (var p in result.Pages)
            {
                var content = SliceBySpans(result.Content, p.Spans);

                // MatchEvaluator counts and replaces in one regex pass each - same cost as
                // the plain .Replace(content, "...") this replaced, not two scans per pattern.
                var pageNoiseCount = 0;
                content = NoiseCommentLineRegex.Replace(content, _ => { pageNoiseCount++; return ""; });
                if (pageNoiseCount > 0)
                {
                    noiseCommentsStripped += pageNoiseCount;
                    pagesWithNoiseComments++;
                }

                var pageSetextCount = 0;
                content = SetextTitleRegex.Replace(content, m =>
                {
                    pageSetextCount++;
                    return "# " + m.Groups["title"].Value;
                });
                setextNormalizedCount += pageSetextCount;

                content = content.Trim('\r', '\n');

                if (content.Length == 0)
                {
                    _logger.LogWarning(
                        "'{Blob}' page {Page} has no content (no Spans) - an empty page could reach the index unnoticed.",
                        blobName, p.PageNumber);
                    warnings.Add(new AnalysisWarning(
                        "EmptyPageContent",
                        $"Page {p.PageNumber} has no content (no Spans) - an empty page could reach the index unnoticed.",
                        blobName));
                }

                var openCount  = TableOpenTagRegex.Matches(content).Count;
                var closeCount = TableCloseTagRegex.Matches(content).Count;
                if (openCount != closeCount)
                {
                    _logger.LogWarning(
                        "'{Blob}' page {Page} has unbalanced <table> tags ({Open} open, {Close} close) - likely split across a page boundary.",
                        blobName, p.PageNumber, openCount, closeCount);
                    warnings.Add(new AnalysisWarning(
                        "UnbalancedTableTags",
                        $"Page {p.PageNumber} has {openCount} <table> open tag(s) but {closeCount} close tag(s) - likely split across a page boundary.",
                        blobName));
                }

                pages.Add(new PdfPageRecord
                {
                    BlobName    = blobName,
                    PageNumber  = p.PageNumber,
                    PageContent = content,
                    Title       = title,
                });
            }

            // File-level summaries (not per-page) - measure how much DI decoration/cosmetic
            // normalization this file needed, worth knowing but not worth a warning per page.
            if (setextNormalizedCount > 0)
                warnings.Add(new AnalysisWarning(
                    "SetextTitleNormalized",
                    $"Setext-style title normalized to ATX on {setextNormalizedCount} page(s).",
                    blobName));

            if (noiseCommentsStripped > 0)
                warnings.Add(new AnalysisWarning(
                    "NoiseCommentsStripped",
                    $"{noiseCommentsStripped} DI decoration comment(s) (page header/footer/number/figure-content) stripped across {pagesWithNoiseComments} page(s).",
                    blobName));

            return (pages, warnings);
        }

        // Matches a whole "<!-- PageHeader="..." -->"-style line (also PageFooter/
        // PageNumber/FigureContent) - anchored to a full line so a document that happens
        // to contain this literal text in its own prose isn't eaten by accident. The
        // quoted value uses (?:[^"\\]|\\.)* rather than a lazy ".*?" so an escaped quote
        // inside it doesn't truncate the match early.
        private static readonly Regex NoiseCommentLineRegex = new(
            @"^[ \t]*<!--\s*(?:Page(?:Header|Footer|Number)|FigureContent)\s*=\s*""(?:[^""\\]|\\.)*""\s*-->[ \t]*\r?\n?",
            RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex TableOpenTagRegex  = new(@"<table\b",    RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TableCloseTagRegex = new(@"</table\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // DI renders the document Title as setext ("Title" line + "===" underline), not
        // ATX ("# "), unlike every other heading. Scoped to "=" underlines only - "-"
        // underlines are ambiguous with a markdown thematic break (<hr>), so those are
        // deliberately left alone rather than risk mis-normalizing an actual rule.
        private static readonly Regex SetextTitleRegex = new(
            @"^(?<title>[^\n]+)\r?\n=+[ \t]*$", RegexOptions.Multiline | RegexOptions.Compiled);

        private static string SliceBySpans(string content, IReadOnlyList<DocumentSpan>? spans)
        {
            if (spans is not { Count: > 0 }) return "";
            return string.Concat(spans.OrderBy(s => s.Offset).Select(s => content.Substring(s.Offset, s.Length)));
        }

        // Paragraphs DI classified as Title/SectionHeading only - real section structure,
        // not incidental roles like headers/footers (see GetBoilerplate).
        // - Offset/PageNumber come from Spans/BoundingRegions: DocumentParagraph has no
        //   PageNumber property of its own.
        private IReadOnlyList<Heading> GetHeadings(AnalyzeResult result) =>
            result.Paragraphs
                .Where(p => p.Role == ParagraphRole.Title || p.Role == ParagraphRole.SectionHeading)
                .Select(ToHeading)
                .ToList();

        // Paragraphs DI classified as repeated boilerplate (page header/footer, footnote,
        // page number) - kept separate from GetHeadings so "Headings" only ever means
        // real section structure.
        // - PageNumber role is included here, not split into its own bucket: it's just
        //   per-page furniture like header/footer. Without it, PageNumber paragraphs fell
        //   through both buckets and vanished silently.
        private IReadOnlyList<Heading> GetBoilerplate(AnalyzeResult result) =>
            result.Paragraphs
                .Where(p => p.Role == ParagraphRole.PageHeader || p.Role == ParagraphRole.PageFooter
                         || p.Role == ParagraphRole.Footnote || p.Role == ParagraphRole.PageNumber)
                .Select(ToHeading)
                .ToList();

        private static Heading ToHeading(DocumentParagraph p) => new(
            p.Content,
            p.Role.ToString()!,
            p.Spans is { Count: > 0 } ps ? ps[0].Offset : null,
            p.BoundingRegions is { Count: > 0 } br ? br[0].PageNumber : 0);

        // Each page's width/height/unit as measured by DI - not the PDF's own MediaBox.
        private IReadOnlyList<PageDimensions> GetPageDimensions(AnalyzeResult result) =>
            result.Pages
                .Select(p => new PageDimensions(p.PageNumber, p.Width, p.Height, p.Unit.ToString() ?? ""))
                .ToList();

        // Every table: cells' row/column position, kind (e.g. columnHeader vs. content),
        // and row/column span for merged cells.
        // - Offset/PageNumber: same Spans/BoundingRegions pattern as GetHeadings.
        // - RowSpan/ColumnSpan: null for an ordinary cell; without them a merged header
        //   cell would look like a missing cell downstream.
        // - Offset is null, not 0, when Spans is empty - 0 is a valid real offset, so it
        //   can't double as "unknown" too.
        private IReadOnlyList<TableInfo> GetTables(AnalyzeResult result) =>
            result.Tables
                .Select(t => new TableInfo(
                    t.RowCount,
                    t.ColumnCount,
                    t.Cells.Select(c => new TableCellInfo(c.RowIndex, c.ColumnIndex, c.Kind.ToString() ?? "", c.Content, c.RowSpan, c.ColumnSpan)).ToList(),
                    t.Spans is { Count: > 0 } ts ? ts[0].Offset : null,
                    t.BoundingRegions is { Count: > 0 } br ? br[0].PageNumber : 0))
                .ToList();

        // Every checkbox/radio button: selected state, DI's confidence, bounding polygon.
        // - Offset comes from Span (singular) - a selection mark has exactly one
        //   position, unlike paragraphs/tables which use the plural Spans.
        // - Confidence/Polygon are free fields on the same object already read for
        //   State/Offset.
        private IReadOnlyList<SelectionMarkInfo> GetSelectionMarks(AnalyzeResult result) =>
            result.Pages
                .SelectMany(p => p.SelectionMarks.Select(sm => new SelectionMarkInfo(
                    p.PageNumber, sm.State.ToString(), sm.Span.Offset, sm.Confidence, ToPolygonPoints(sm.Polygon))))
                .ToList();

        // Every figure DI detected:
        // - Caption text, if present.
        // - Id - only needed to later fetch the cropped image via the figures output endpoint.
        // - Elements - JSON-pointer refs into paragraphs that discuss/describe the figure,
        //   broader than just its Caption.
        // All free as part of prebuilt-layout - no add-on feature required.
        private IReadOnlyList<FigureInfo> GetFigures(AnalyzeResult result) =>
            result.Figures
                .Select(f => new FigureInfo(
                    f.Caption?.Content,
                    f.Spans is { Count: > 0 } fs ? fs[0].Offset : null,
                    f.BoundingRegions is { Count: > 0 } br ? br[0].PageNumber : 0,
                    f.Id,
                    f.Elements ?? []))
                .ToList();

        // Every OCR-detected line, with its bounding polygon - the most granular
        // positional data DI offers for free.
        // - Future highlight-on-source join (see LineInfo/PageDimensions): a chunk's span
        //   range selects its lines by Offset, their Polygons union into the highlight region.
        // - By far the bulkiest structure here (every line, every page). Not persisted
        //   permanently today (dev-only reports only) - correct until source-grounding ships.
        private IReadOnlyList<LineInfo> GetLines(AnalyzeResult result) =>
            result.Pages
                .SelectMany(p => p.Lines.Select(line => new LineInfo(
                    line.Content,
                    line.Spans is { Count: > 0 } ls ? ls[0].Offset : null,
                    p.PageNumber,
                    ToPolygonPoints(line.Polygon))))
                .ToList();

        // DI returns a polygon as a flat [x1, y1, x2, y2, ...] float list rather than typed
        // points - paired up here into PolygonPoint so callers don't have to know that.
        private static IReadOnlyList<PolygonPoint> ToPolygonPoints(IReadOnlyList<float>? polygon)
        {
            if (polygon is not { Count: > 0 }) return [];

            var points = new List<PolygonPoint>(polygon.Count / 2);
            for (var i = 0; i + 1 < polygon.Count; i += 2)
                points.Add(new PolygonPoint(polygon[i], polygon[i + 1]));
            return points;
        }

        // Every DI-detected section - the closest thing prebuilt-layout offers to real
        // semantic chunk boundaries, vs. the page-only boundaries GetPages relies on today.
        // - Every Span captured (not anchor-only like GetHeadings/GetTables/GetFigures): a
        //   section only means something as a start-to-end range.
        // - Elements stay as DI's raw JSON-pointer strings - resolving them into actual
        //   content is a future chunk-builder's job.
        private IReadOnlyList<SectionInfo> GetSections(AnalyzeResult result) =>
            result.Sections
                .Select(s => new SectionInfo(
                    s.Spans.Select(sp => new SectionSpan(sp.Offset, sp.Length)).ToList(),
                    s.Elements.ToList()))
                .ToList();

        // Below this, a page's OCR is likely garbled enough to be worth a human look -
        // the single most actionable content-quality signal this class produces.
        private const double MinAcceptablePageConfidence = 0.85;

        // Average OCR word-confidence per page - a data-quality signal only ("flag for
        // review"), never a chunk-boundary signal (that's GetSections). Pages with zero
        // detected words are omitted from Quality, not reported as 0.0 confidence - that
        // would misleadingly suggest DI is unsure about content that isn't there. They
        // still get their own warning below (likely an image-only/scanned page) instead
        // of silently vanishing.
        // internal (not private): see StructureWarnings' note - testable without a live DI call.
        internal (IReadOnlyList<PageQuality> Quality, IReadOnlyList<AnalysisWarning> Warnings) GetPageQuality(
            AnalyzeResult result, string blobName)
        {
            var quality  = new List<PageQuality>();
            var warnings = new List<AnalysisWarning>();

            foreach (var p in result.Pages)
            {
                if (p.Words.Count == 0)
                {
                    warnings.Add(new AnalysisWarning(
                        "ZeroWordsOnPage",
                        $"Page {p.PageNumber} has zero detected words - likely an image-only/scanned page.",
                        blobName));
                    continue;
                }

                var confidence = p.Words.Average(w => (double)w.Confidence);
                quality.Add(new PageQuality(p.PageNumber, confidence));

                if (confidence < MinAcceptablePageConfidence)
                    warnings.Add(new AnalysisWarning(
                        "LowPageConfidence",
                        $"Page {p.PageNumber}: avg OCR word confidence {confidence:F2}, below the {MinAcceptablePageConfidence:F2} threshold.",
                        blobName));
            }

            return (quality, warnings);
        }

        // DI's own non-fatal warnings (e.g. a page that partially failed OCR) - distinct
        // from the zero-pages case, which is treated as an outright failure. Wraps
        // Azure's DocumentIntelligenceWarning so callers don't need the SDK type.
        private IReadOnlyList<AnalysisWarning> GetWarnings(AnalyzeResult result) =>
            result.Warnings
                .Select(w => new AnalysisWarning(w.Code, w.Message, w.Target))
                .ToList();
    }
}
