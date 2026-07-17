using Azure;
using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services
{
    // Handles everything Document Intelligence (DI) needs to do with one PDF, except for
    // preflight checks and PdfPig-native reads. Specifically, this class:
    // - Makes the one paid "analyze" call to DI, retrying automatically if throttled (429).
    // - Assembles the DI response into markdown-formatted pages.
    // - Extracts every DI structural feature (headings, boilerplate, tables, page
    //   dimensions, selection marks, figures, handwritten spans, lines) into
    //   PdfDocumentStructure, maximizing what this extraction step captures.
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
        //    figures, handwritten spans, lines) from the same result.
        public async Task<PDFStructureExtractorResult> AnalyzeDocumentAsync(
            byte[] pdfBytes, string blobName, DocMetadata nativeMetadata, CancellationToken ct = default)
        {
            var analyzeResults = await DIAnalyzeDocumentAsync(pdfBytes, blobName, ct);
            if (!analyzeResults.Ok)
                return new PDFStructureExtractorResult(false, null, null, null, null, analyzeResults.Error);

            var analysis = analyzeResults.Result!; // AnalyzeDocumentAsync guarantees Ok=true only for a non-empty analysis

            // Title: prefers the PDF's own Info-dictionary Title (nativeMetadata.Title) when
            // the file actually has one set - real PDF metadata, not a guess. Falls back to
            // a blob-name-derived title otherwise.
            var title = !string.IsNullOrWhiteSpace(nativeMetadata.Title)
                ? nativeMetadata.Title
                : blobName.Split('/')[0]
                    .Replace(".pdf", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("-", " ");

            return new PDFStructureExtractorResult(
                true, 
                analysis.Content, 
                GetPages(analysis, blobName, title),
                new PdfDocumentStructure(
                GetHeadings(analysis),
                GetBoilerplate(analysis),
                GetTables(analysis),
                GetPageDimensions(analysis),
                GetSelectionMarks(analysis),
                GetFigures(analysis),
                GetHandwrittenSpans(analysis),
                GetLines(analysis)),
                analysis.Pages!.Count * CostPerPage, null);
        }

        // Makes the single paid call to Document Intelligence.
        // - On a 429 (throttled) response, waits and retries using Microsoft's recommended
        //   backoff schedule: 2s, then 5s, then 13s, then 34s.
        // - Why this retry loop is needed: WaitUntil.Completed polls DI internally, and a
        //   known SDK bug (Azure/azure-sdk-for-net#50904) means that internal polling can
        //   still hit 429 even if DocumentIntelligenceClientOptions.Retry was configured
        //   when the client was built. Without this loop, that would surface as an
        //   unhandled exception.
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
        private async Task<AnalyzeOutcome> AnalyzeDocumentAsync(byte[] pdfBytes, string blobName, CancellationToken ct)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    _logger.LogInformation("Submitting '{Blob}' to Document Intelligence (attempt {Attempt}).", blobName, attempt + 1);

                    // Markdown output (vs. DI's default plain text) is what lets
                    // PDFMarkdownExtractor split on DI's own page/table/heading structure
                    // instead of re-deriving it from paragraph roles and span offsets.
                    var analyzeOptions = new AnalyzeDocumentOptions("prebuilt-layout", BinaryData.FromBytes(pdfBytes))
                    {
                        OutputContentFormat = DocumentContentFormat.Markdown,
                    };

                    Operation<AnalyzeResult> operation = await _diClient.AnalyzeDocumentAsync(
                        WaitUntil.Completed, analyzeOptions, cancellationToken: ct);

                    var result = operation.Value;
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

                    return new AnalyzeOutcome(true, result, null);
                }
                catch (RequestFailedException ex) when (ex.Status == 429 && attempt < BackoffDelays.Length)
                {
                    var wait = BackoffDelays[attempt];
                    _logger.LogWarning("DI throttled '{Blob}' (attempt {Attempt}); backing off {Wait}.", blobName, attempt + 1, wait);
                    await Task.Delay(wait, ct);
                }
                catch (RequestFailedException ex)
                {
                    _logger.LogWarning(ex, "Document Intelligence failed to analyze '{Blob}'.", blobName);
                    return new AnalyzeOutcome(false, null, new ExtractionError
                    {
                        DocumentId = blobName,
                        Message = $"Document Intelligence request failed ({ex.Status}): {ex.Message}",
                        Reason = ex.Status == 429 ? PdfOpenFailureReason.Throttled : PdfOpenFailureReason.DiServiceError,
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Unexpected error analyzing '{Blob}' with Document Intelligence.", blobName);
                    return new AnalyzeOutcome(false, null, new ExtractionError
                    {
                        DocumentId = blobName,
                        Message = $"Document Intelligence analysis failed unexpectedly: {ex.Message}",
                        Reason = PdfOpenFailureReason.Unknown,
                    });
                }
            }
        }

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
        // header, page footer, footnote) rather than a real heading - the same roles
        // PDFMarkdownExtractor.NoiseCommentLineRegex already strips out of page content as
        // noise. Kept separate from GetHeadings so "Headings" only ever means real section
        // structure.
        public IReadOnlyList<Heading> GetBoilerplate(AnalyzeResult result) =>
            result.Paragraphs
                .Where(p => p.Role == ParagraphRole.PageHeader || p.Role == ParagraphRole.PageFooter || p.Role == ParagraphRole.Footnote)
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

        // Returns every table, including each cell's row/column position and kind
        // (e.g. columnHeader vs. regular content).
        // - Offset and PageNumber follow the same Spans/BoundingRegions pattern used in
        //   GetHeadings, since DocumentTable also has no PageNumber property of its own.
        public IReadOnlyList<TableInfo> GetTables(AnalyzeResult result) =>
            result.Tables
                .Select(t => new TableInfo(
                    t.RowCount,
                    t.ColumnCount,
                    t.Cells.Select(c => new TableCellInfo(c.RowIndex, c.ColumnIndex, c.Kind.ToString() ?? "", c.Content)).ToList(),
                    t.Spans is { Count: > 0 } ts ? ts[0].Offset : 0,
                    t.BoundingRegions is { Count: > 0 } br ? br[0].PageNumber : 0))
                .ToList();

        // Returns every checkbox/radio button on every page, with its selected/unselected
        // state.
        // - Offset comes from Span (singular), since a selection mark has exactly one
        //   position - unlike paragraphs/tables, which use the plural Spans.
        public IReadOnlyList<SelectionMarkInfo> GetSelectionMarks(AnalyzeResult result) =>
            result.Pages
                .SelectMany(p => p.SelectionMarks.Select(sm => new SelectionMarkInfo(p.PageNumber, sm.State.ToString(), sm.Span.Offset)))
                .ToList();

        // Returns every figure (image/diagram) DI detected, with its caption text if it has
        // one. Free as part of prebuilt-layout - no add-on feature required.
        public IReadOnlyList<FigureInfo> GetFigures(AnalyzeResult result) =>
            result.Figures
                .Select(f => new FigureInfo(
                    f.Caption?.Content,
                    f.Spans is { Count: > 0 } fs ? fs[0].Offset : 0,
                    f.BoundingRegions is { Count: > 0 } br ? br[0].PageNumber : 0))
                .ToList();

        // Returns every span of handwritten text DI detected, with DI's own confidence
        // score attached rather than pre-filtered by a hardcoded threshold - callers decide
        // what confidence is "good enough" for their use case.
        // - "Styles" entries only point at spans within result.Content; they don't carry
        //   the text itself, so the actual string has to be extracted with Substring.
        public IReadOnlyList<HandwrittenSpan> GetHandwrittenSpans(AnalyzeResult result) =>
            result.Styles
                .Where(s => s.IsHandwritten == true)
                .SelectMany(s => s.Spans.Select(span => new HandwrittenSpan(
                    result.Content.Substring(span.Offset, span.Length),
                    span.Offset,
                    s.Confidence)))
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
    }
}
