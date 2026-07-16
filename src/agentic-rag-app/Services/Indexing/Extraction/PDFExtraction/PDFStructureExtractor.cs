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
    // - Extracts every DI structural feature (headings, tables, page dimensions, selection marks)
    //   so a later step can use them to build ChunkMetadata.
    public sealed class PDFStructureExtractor
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
        private readonly PDFMarkdownExtractor _markdownExtractor;

        public PDFStructureExtractor(DocumentIntelligenceClient diClient, ILogger<PDFStructureExtractor> logger, PDFMarkdownExtractor markdownExtractor)
        {
            _diClient = diClient;
            _logger = logger;
            _markdownExtractor = markdownExtractor;
        }

        // Main entry point, called once preflight/native-metadata reading has already happened.
        // Expects the caller (DocumentIntelligenceExtractor) to have already:
        // - Validated the PDF (PdfDocumentValidator.IsPDFValid).
        // - Read nativeMetadata/bookmarks via PdfMetadataExtractor.ParseNativeMetadata and
        //   closed the PdfDocument - this method receives only the resulting data, never
        //   the PdfDocument object itself.
        // Steps performed here:
        // 1. Submit the PDF to Document Intelligence for analysis.
        // 2. If that call fails, return immediately with Ok=false and a typed ExtractionError.
        // 3. Otherwise, build markdown pages and extract every structural feature
        //    (headings, tables, page dimensions, selection marks) from the same result.
        public async Task<PdfStructureExtraction> ExtractPdfStructureAsync(
            byte[] pdfBytes, string blobName, DocMetadata nativeMetadata, CancellationToken ct = default)
        {
            var analyzeOutcome = await AnalyzeDocumentAsync(pdfBytes, blobName, ct);
            if (!analyzeOutcome.Ok)
                return new PdfStructureExtraction(false, null, null, null, analyzeOutcome.Error);

            var analysis  = analyzeOutcome.Result!;
            var pageCount = analysis.Pages?.Count ?? 0;

            // PdfDocumentValidator's preflight already rejected zero-page PDFs (via PdfPig),
            // so getting here with pageCount == 0 means DI itself analyzed the document but
            // produced no pages - a DI-side failure mode, not something to silently treat as
            // a successful-but-empty extraction (that would otherwise surface much later, as
            // an unexplained "no pages, won't be indexed" red flag in PdfPipelineValidator).
            if (pageCount == 0)
            {
                _logger.LogWarning("Document Intelligence returned zero pages for '{Blob}'.", blobName);
                return new PdfStructureExtraction(false, null, null, null, new ExtractionError
                {
                    DocumentId = blobName,
                    Message = "Document Intelligence analysis returned zero pages.",
                    Reason = PdfOpenFailureReason.EmptyDocument,
                });
            }

            // Title: prefers the PDF's own Info-dictionary Title (nativeMetadata.Title) when
            // the file actually has one set - real PDF metadata, not a guess. Falls back to
            // a blob-name-derived title otherwise.
            var title = !string.IsNullOrWhiteSpace(nativeMetadata.Title)
                ? nativeMetadata.Title
                : blobName.Split('/')[0]
                    .Replace(".pdf", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("-", " ");

            var pages = _markdownExtractor.BuildMarkdownPages(blobName, analysis, pageCount, title, nativeMetadata.Bookmarks);

            var metadata = new PdfStructureMetadata(
                nativeMetadata,
                GetHeadings(analysis),
                nativeMetadata.Bookmarks,
                GetTables(analysis),
                GetPageDimensions(analysis),
                GetSelectionMarks(analysis));

            return new PdfStructureExtraction(true, pages, metadata, pageCount * CostPerPage, null);
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

                    return new AnalyzeOutcome(true, operation.Value, null);
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

        // Returns every heading/section paragraph - i.e. paragraphs DI classified with a
        // structural role (title, sectionHeading, pageHeader, footnote, etc.) rather than
        // as plain body text.
        // - Offset and PageNumber are read from Spans/BoundingRegions, because
        //   DocumentParagraph has no PageNumber property of its own (same approach
        //   BuildMarkdownPages uses elsewhere).
        public IReadOnlyList<Heading> GetHeadings(AnalyzeResult result) =>
            result.Paragraphs
                .Where(p => p.Role is not null)
                .Select(p => new Heading(
                    p.Content,
                    p.Role.ToString()!,
                    p.Spans is { Count: > 0 } ps ? ps[0].Offset : 0,
                    p.BoundingRegions is { Count: > 0 } br ? br[0].PageNumber : 0))
                .ToList();

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

        // Returns every span of handwritten text with confidence above 0.8
        // (the same threshold used in the original quickstart sample).
        // - "Styles" entries only point at spans within result.Content; they don't carry
        //   the text itself, so the actual string has to be extracted with Substring.
        public IReadOnlyList<string> GetHandwrittenContent(AnalyzeResult result) =>
            result.Styles
                .Where(s => s.IsHandwritten == true && s.Confidence > 0.8)
                .SelectMany(s => s.Spans)
                .Select(span => result.Content.Substring(span.Offset, span.Length))
                .ToList();
    }

    
}
