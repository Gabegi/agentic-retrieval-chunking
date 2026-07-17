using System.Diagnostics.CodeAnalysis;
using System.IO;
using Azure;
using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services
{
    // Handles everything Document Intelligence (DI) needs to do with one PDF, except for
    // preflight checks and PdfPig-native reads. Specifically, this class:
    // - Makes the one paid "analyze" call to DI, retrying automatically if throttled (429),
    //   and verifying the response is actually markdown before trusting any offset in it.
    // - Assembles the DI response into markdown-formatted pages.
    // - Extracts every DI structural feature (headings, boilerplate, tables, page
    //   dimensions, selection marks, figures, sections, page quality, lines) into
    //   PdfDocumentStructure, maximizing what this extraction step captures.
    // - Surfaces non-fatal analysis warnings DI attached to the document.
    public sealed class PDFDocumentAnalyzer
    {
        // Cost per page for Azure's "prebuilt-layout" model:
        // - Priced at $10 per 1,000 pages, i.e. $0.01/page, as of when this was written.
        // - Check current Azure pricing before trusting any cost estimate based on this constant.
        private const decimal CostPerPage = 0.01m;

        private static readonly TimeSpan[] BackoffDelays =
        {
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(13), TimeSpan.FromSeconds(34)
        };

        // Ordinary (non-throttled) interval between status-poll GETs, once the analyze job
        // is running - unrelated to BackoffDelays, which only applies after a 429.
        private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(2);

        private readonly DocumentIntelligenceClient _diClient;
        private readonly ILogger _logger;

        public PDFDocumentAnalyzer(DocumentIntelligenceClient diClient, ILogger<PDFDocumentAnalyzer> logger)
        {
            _diClient = diClient;
            _logger = logger;
        }

        // Main entry point, called once preflight/native-metadata reading has already happened.
        // Expects the caller (DocumentIntelligenceExtractor) to have already:
        // - Validated the PDF (PdfDocumentValidator.IsPDFValid).
        // - Read nativeMetadata/bookmarks via PdfNativeMetadataExtractor.ExtractPdfNativeMetadata and
        //   closed the PdfDocument - this method receives only the resulting data, never
        //   the PdfDocument object itself.
        // Steps performed here:
        // 1. Submit the PDF to Document Intelligence for analysis.
        // 2. If that call fails, return immediately with Ok=false and a typed ExtractionError.
        // 3. Otherwise, build markdown pages and extract every structural feature DI offers
        //    for free (headings, boilerplate, tables, page dimensions, selection marks,
        //    figures, sections, page quality, lines) from the same result.
        public async Task<PDFStructureExtractorResult> AnalyzeDocumentAsync(
            byte[] pdfBytes, string blobName, DocMetadata nativeMetadata, CancellationToken ct = default)
        {
            var analyzeResults = await DIAnalyzeDocumentAsync(pdfBytes, blobName, ct);

            if (!TryValidateAnalyzeOutcome(analyzeResults, blobName, out var analysis, out var error))
                return new PDFStructureExtractorResult(false, null, null, null, null, error);

            // Reused for EstimatedCostUsd below instead of analysis.Pages!.Count - avoids a
            // null-forgiving operator on the SDK's nullable Pages property by reusing an
            // already-materialized, already non-null list with the same count.
            var pages = GetPages(analysis, blobName, GetTitle(nativeMetadata, blobName));

            return new PDFStructureExtractorResult(
                true,
                analysis.Content,
                pages,
                new PdfDocumentStructure(
                GetHeadings(analysis),
                GetBoilerplate(analysis),
                GetTables(analysis),
                GetPageDimensions(analysis),
                GetSelectionMarks(analysis),
                GetFigures(analysis),
                GetLines(analysis),
                GetSections(analysis),
                GetPageQuality(analysis)),
                pages.Count * CostPerPage, null)
            {
                // DI's own top-level warnings, plus whatever the analyze call itself flagged
                // (e.g. non-BMP characters) - merged into one list so callers only ever have
                // one place to look, regardless of which stage a warning came from.
                Warnings = analyzeResults.Warnings.Concat(GetWarnings(analysis)).ToList(),
            };
        }

        // Makes the single paid call to Document Intelligence - submitting exactly once,
        // no matter how much throttling happens afterward.
        // - Why WaitUntil.Started, not WaitUntil.Completed: WaitUntil.Completed polls DI
        //   internally, and a known SDK bug (Azure/azure-sdk-for-net#50904) means that
        //   internal polling can still hit 429 even if DocumentIntelligenceClientOptions.Retry
        //   was configured when the client was built. The previous version of this method
        //   caught that 429 and retried by calling AnalyzeDocumentAsync again from
        //   scratch - but the original operation was already running server-side, so every
        //   retry submitted a brand-new, separately-billed analysis (up to 5x cost under
        //   sustained throttling). WaitUntil.Started returns as soon as the POST succeeds;
        //   everything from here on only ever retries the free status-poll GET, never the
        //   paid POST.
        // - On a 429 from a status poll, waits and retries using Microsoft's recommended
        //   backoff schedule: 2s, then 5s, then 13s, then 34s. PollingInterval (not
        //   BackoffDelays) governs the ordinary, non-throttled wait between polls.
        // - Any RequestFailedException, plus any other unexpected exception (SDK bug,
        //   deserialization failure, etc.), is caught and returned as Ok=false with a
        //   typed reason, instead of throwing. OperationCanceledException is the one
        //   deliberate exception to that: it means ct itself fired (e.g. host shutdown),
        //   not a per-document failure, so it's left to propagate and stop the whole run
        //   rather than being recorded as this one document failing to extract.
        // - A zero-page result also comes back as Ok=false: PdfDocumentValidator's preflight
        //   already rejected zero-page PDFs (via PdfPig), so DI itself returning zero pages
        //   here means DI-side analysis failed to produce anything usable - not something
        //   callers should treat as a successful-but-empty analysis (that would otherwise
        //   surface much later, as an unexplained "no pages, won't be indexed" red flag in
        //   PdfPipelineValidator). This is why Ok=true guarantees Result.Pages is non-empty.
        private async Task<AnalyzeOutcome> DIAnalyzeDocumentAsync(byte[] pdfBytes, string blobName, CancellationToken ct)
        {
            _logger.LogInformation("Submitting '{Blob}' to Document Intelligence.", blobName);

            // Markdown output (vs. DI's default plain text) is what lets
            // PDFMarkdownExtractor split on DI's own page/table/heading structure instead
            // of re-deriving it from paragraph roles and span offsets.
            var analyzeOptions = new AnalyzeDocumentOptions("prebuilt-layout", BinaryData.FromBytes(pdfBytes))
            {
                OutputContentFormat = DocumentContentFormat.Markdown,
            };

            Operation<AnalyzeResult> operation;
            try
            {
                operation = await _diClient.AnalyzeDocumentAsync(WaitUntil.Started, analyzeOptions, cancellationToken: ct);
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

            for (var attempt = 0; !operation.HasCompleted; attempt++)
            {
                try
                {
                    await operation.UpdateStatusAsync(ct);
                }
                catch (RequestFailedException ex) when (ex.Status == 429 && attempt < BackoffDelays.Length)
                {
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

        // Single place that decides whether a raw DI response is usable, and collects
        // every non-fatal thing worth flagging about it along the way - format check,
        // non-BMP character check, zero-pages check, in that order (cheapest/most
        // fundamental first). Kept separate from DIAnalyzeDocumentAsync's retry loop so
        // that loop only has to care about "call succeeded, now validate the result",
        // not each individual validation rule.
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

        // Decides whether analyzeResults is actually usable - folding both failure signals
        // that matter into one check, since callers only ever have one real question
        // ("do I have a usable analysis or not"), not two. Same Try(out, out) +
        // [NotNullWhen] shape as PdfDocumentValidator.IsPDFValid/TryOpenAndValidate: the
        // compiler proves result is non-null on true and error is non-null on false,
        // instead of the caller trusting a bare `analyzeResults.Result!`.
        // - Ok=false: a reported, already-logged failure - Error is already set by whichever
        //   check inside DIAnalyzeDocumentAsync/ValidateAnalyzeResult rejected it.
        // - Ok=true but Result=null: the invariant those methods are supposed to enforce
        //   (Ok=true always comes with a non-null Result) didn't hold. This should never
        //   trip, but a future bug in either of those methods would otherwise surface as an
        //   unhandled NullReferenceException several lines downstream instead of the typed
        //   ExtractionError every other failure in this class produces. LogError (not the
        //   LogWarning the rest of this class uses) is deliberate: this signals a bug in
        //   our own code, not a routine DI-side failure.
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

                // Same "don't trust the invariant silently" reasoning as the Result=null
                // case below: Ok=false is supposed to always come with a non-null Error.
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

        // Confirms DI actually returned markdown - the trust boundary every Offset field in
        // this file depends on. Every record below indexes into analysis.Content assuming
        // it's markdown-formatted (see the OutputContentFormat comment above); if DI ever
        // returned Text instead, every offset would still "work" but silently point at the
        // wrong characters, producing garbled content several steps downstream instead of
        // an obvious failure here. Turning that assumption into a hard check here - right
        // where retry/error handling already exists - is cheaper than debugging offset
        // drift later.
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

        // Diagnostic-only: counts characters in the returned content that need a UTF-16
        // surrogate pair (i.e. codepoints above the Basic Multilingual Plane - emoji, some
        // math/technical symbols - not ordinary Dutch diacritics, which all fit in one
        // UTF-16 unit same as plain ASCII). Every Offset field in this file assumes DI's
        // offsets are UTF-16 code-unit offsets, exactly what string.Substring expects; a
        // surrogate pair alone doesn't prove that assumption is broken for this document,
        // it's just the signal worth a closer look if garbled content ever shows up
        // downstream. Doesn't fail extraction - logged immediately for real-time
        // visibility, and returned as an AnalysisWarning so it also reaches the same
        // blob-stored validation report every other warning does.
        // - This is a heuristic, not an exact surrogate-pair count: EnumerateRunes()
        //   replaces a malformed/unpaired lone surrogate with a single U+FFFD replacement
        //   rune, so a lone surrogate contributes 0 to Length-minus-RuneCount even though
        //   it's arguably a more interesting anomaly (actual encoding corruption) than a
        //   well-formed pair. Exact only for well-formed strings - good enough for "should
        //   I take a closer look", not for a precise count of every non-BMP occurrence.
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

        // Prefers the PDF's own Info-dictionary Title (nativeMetadata.Title) when the file
        // actually has one set - real PDF metadata, not a guess. Falls back to a
        // blob-name-derived title otherwise.
        private static string GetTitle(DocMetadata nativeMetadata, string blobName) =>
            !string.IsNullOrWhiteSpace(nativeMetadata.Title)
                ? nativeMetadata.Title
                : blobName.Split('/')[0]
                    .Replace(".pdf", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("-", " ");

        // Returns one PdfPageRecord per PDF page, sliced directly from analysis.Content
        // using each DocumentPage's own Spans - DI's structural page model - rather than
        // splitting the content string on its "<!-- PageBreak -->" text marker. Page
        // boundaries come from DI's own per-page data, so there's no possibility of a
        // fragment-count-vs-pageCount mismatch to guard against.
        // - Does not carry over what PDFMarkdownExtractor.BuildMarkdownPages also did:
        //   bookmark breadcrumbs, cross-page heading carry-forward, noise-comment
        //   stripping (PageHeader/PageFooter/PageNumber/FigureContent), setext-title
        //   normalization, or repairing a <table> that DI split across two pages' Spans.
        public IReadOnlyList<PdfPageRecord> GetPages(AnalyzeResult result, string blobName, string title) =>
            result.Pages
                .Select(p => new PdfPageRecord
                {
                    BlobName    = blobName,
                    PageIndex   = p.PageNumber,
                    PageContent = SliceBySpans(result.Content, p.Spans),
                    Title       = title,
                })
                .ToList();

        private static string SliceBySpans(string content, IReadOnlyList<DocumentSpan>? spans)
        {
            if (spans is not { Count: > 0 }) return "";
            return string.Concat(spans.OrderBy(s => s.Offset).Select(s => content.Substring(s.Offset, s.Length)));
        }

        // Returns every true heading/title paragraph - i.e. paragraphs DI classified as
        // "title" or "sectionHeading", not incidental structural roles like page headers/
        // footers/footnotes (see GetBoilerplate for those).
        // - Offset and PageNumber are read from Spans/BoundingRegions, because
        //   DocumentParagraph has no PageNumber property of its own (same approach
        //   BuildMarkdownPages uses elsewhere).
        public IReadOnlyList<Heading> GetHeadings(AnalyzeResult result) =>
            result.Paragraphs
                .Where(p => p.Role == ParagraphRole.Title || p.Role == ParagraphRole.SectionHeading)
                .Select(ToHeading)
                .ToList();

        // Returns every paragraph DI classified as repeated-boilerplate structure (page
        // header, page footer, footnote, page number) rather than a real heading - the same
        // roles PDFMarkdownExtractor.NoiseCommentLineRegex already strips out of page
        // content as noise. Kept separate from GetHeadings so "Headings" only ever means
        // real section structure.
        // - PageNumber is included here (not a bug fix worth its own bucket): it's
        //   repeated-per-page furniture in the same spirit as header/footer, not a distinct
        //   category anything downstream currently needs split out. Without it, paragraphs
        //   DI tags PageNumber fell through both GetHeadings and GetBoilerplate and vanished
        //   silently.
        public IReadOnlyList<Heading> GetBoilerplate(AnalyzeResult result) =>
            result.Paragraphs
                .Where(p => p.Role == ParagraphRole.PageHeader || p.Role == ParagraphRole.PageFooter
                         || p.Role == ParagraphRole.Footnote || p.Role == ParagraphRole.PageNumber)
                .Select(ToHeading)
                .ToList();

        private static Heading ToHeading(DocumentParagraph p) => new(
            p.Content,
            p.Role.ToString()!,
            p.Spans is { Count: > 0 } ps ? ps[0].Offset : 0,
            p.BoundingRegions is { Count: > 0 } br ? br[0].PageNumber : 0);

        // Returns each page's width/height/unit as measured by DI itself -
        // not the dimensions declared in the PDF's own MediaBox.
        public IReadOnlyList<PageDimensions> GetPageDimensions(AnalyzeResult result) =>
            result.Pages
                .Select(p => new PageDimensions(p.PageNumber, p.Width, p.Height, p.Unit.ToString() ?? ""))
                .ToList();

        // Returns every table, including each cell's row/column position, kind (e.g.
        // columnHeader vs. regular content), and row/column span for merged cells.
        // - Offset and PageNumber follow the same Spans/BoundingRegions pattern used in
        //   GetHeadings, since DocumentTable also has no PageNumber property of its own.
        // - RowSpan/ColumnSpan are null for an ordinary single-cell entry; without them, a
        //   merged header cell would look like a missing cell to anything reconstructing
        //   the table layout from Cells alone.
        public IReadOnlyList<TableInfo> GetTables(AnalyzeResult result) =>
            result.Tables
                .Select(t => new TableInfo(
                    t.RowCount,
                    t.ColumnCount,
                    t.Cells.Select(c => new TableCellInfo(c.RowIndex, c.ColumnIndex, c.Kind.ToString() ?? "", c.Content, c.RowSpan, c.ColumnSpan)).ToList(),
                    t.Spans is { Count: > 0 } ts ? ts[0].Offset : 0,
                    t.BoundingRegions is { Count: > 0 } br ? br[0].PageNumber : 0))
                .ToList();

        // Returns every checkbox/radio button on every page, with its selected/unselected
        // state, DI's own confidence in that reading, and its bounding polygon.
        // - Offset comes from Span (singular), since a selection mark has exactly one
        //   position - unlike paragraphs/tables, which use the plural Spans.
        // - Confidence/Polygon are free fields on the same DocumentSelectionMark object
        //   already being read for State/Offset - not separately fetched.
        public IReadOnlyList<SelectionMarkInfo> GetSelectionMarks(AnalyzeResult result) =>
            result.Pages
                .SelectMany(p => p.SelectionMarks.Select(sm => new SelectionMarkInfo(
                    p.PageNumber, sm.State.ToString(), sm.Span.Offset, sm.Confidence, ToPolygonPoints(sm.Polygon))))
                .ToList();

        // Returns every figure (image/diagram) DI detected, with its caption text if it has
        // one, its Id (needed only to later fetch the cropped image via the figures output
        // endpoint), and Elements - DI's own JSON-pointer refs into the paragraphs that
        // discuss/describe this figure, broader than just its Caption. All free as part of
        // prebuilt-layout - no add-on feature required.
        public IReadOnlyList<FigureInfo> GetFigures(AnalyzeResult result) =>
            result.Figures
                .Select(f => new FigureInfo(
                    f.Caption?.Content,
                    f.Spans is { Count: > 0 } fs ? fs[0].Offset : 0,
                    f.BoundingRegions is { Count: > 0 } br ? br[0].PageNumber : 0,
                    f.Id,
                    f.Elements ?? []))
                .ToList();

        // Returns every OCR-detected line of text on every page, with its bounding polygon -
        // the most granular positional data DI offers for free. Potentially a lot of data
        // (every line, every page); nothing persists this permanently today (dev-only
        // reports only), so that's a future storage-cost concern, not a correctness one.
        public IReadOnlyList<LineInfo> GetLines(AnalyzeResult result) =>
            result.Pages
                .SelectMany(p => p.Lines.Select(line => new LineInfo(
                    line.Content,
                    line.Spans is { Count: > 0 } ls ? ls[0].Offset : 0,
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

        // Returns every DI-detected section - the closest thing prebuilt-layout offers to
        // real semantic chunk boundaries, as opposed to the page-only boundaries GetPages
        // relies on today.
        // - Every Span is captured (not anchor-only like GetHeadings/GetTables/GetFigures):
        //   a section only means something as a start-to-end range, so slicing its content
        //   the way GetPages slices per-page content needs every span, not just the first.
        // - Elements are left as DI's raw JSON-pointer strings; resolving them into actual
        //   paragraphs/tables/figures/subsections is a future chunk-builder's job, not
        //   done here.
        public IReadOnlyList<SectionInfo> GetSections(AnalyzeResult result) =>
            result.Sections
                .Select(s => new SectionInfo(
                    s.Spans.Select(sp => new SectionSpan(sp.Offset, sp.Length)).ToList(),
                    s.Elements.ToList()))
                .ToList();

        // Returns one average word-confidence score per page - a data-quality signal only
        // ("flag this page for review"), never a chunk-boundary signal; boundaries come from
        // GetSections. Pages with zero detected words (e.g. a blank page) are omitted rather
        // than reported as 0.0, since 0 confidence would misleadingly suggest DI is unsure
        // about content that simply isn't there.
        public IReadOnlyList<PageQuality> GetPageQuality(AnalyzeResult result) =>
            result.Pages
                .Where(p => p.Words.Count > 0)
                .Select(p => new PageQuality(p.PageNumber, p.Words.Average(w => (double)w.Confidence)))
                .ToList();

        // Returns every non-fatal warning DI attached to the whole-document analysis (e.g.
        // a page that partially failed OCR) - distinct from the zero-pages case
        // DIAnalyzeDocumentAsync already treats as an outright failure. Wraps Azure's
        // DocumentIntelligenceWarning in this project's own record so callers don't need a
        // reference to the Azure SDK type.
        public IReadOnlyList<AnalysisWarning> GetWarnings(AnalyzeResult result) =>
            result.Warnings
                .Select(w => new AnalysisWarning(w.Code, w.Message, w.Target))
                .ToList();
    }
}
