using Azure;
using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Models;
using System.Security.Cryptography;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Outline;

namespace ProtocolsIndexer.Models
{
    // Small, capability-scoped return types for PDFStructureExtractor - one shape
    // per Get* method, so callers only pull in the fields relevant to what they asked for.

    public sealed record Heading(string Content, string Role);

    public sealed record PageDimensions(int PageNumber, double? Width, double? Height, string Unit);

    public sealed record TableCellInfo(int RowIndex, int ColumnIndex, string Kind, string Content);

    public sealed record TableInfo(int RowCount, int ColumnCount, IReadOnlyList<TableCellInfo> Cells);

    public sealed record SelectionMarkInfo(int PageNumber, string State);

    public sealed record Bookmark(string Title, int Level, int? PageNumber);

    // Result of the one paid DI call. Ok=false carries a typed ExtractionError instead of
    // an unhandled exception, so callers can branch on Reason (Throttled is worth a retry
    // upstream; DiServiceError probably isn't).
    public sealed record AnalyzeOutcome(bool Ok, AnalyzeResult? Result, ExtractionError? Error);
}

namespace ProtocolsIndexer.Services
{
    // Every capability from the DI-vs-PdfPig comparison, one method each. AnalyzeAsync is
    // the only paid call - it runs prebuilt-layout once (with retry on 429) and every
    // Get* method below is a free, synchronous read of the resulting AnalyzeResult. Call
    // AnalyzeAsync once per document, then pass its result into as many Get* methods as
    // you need.
    //
    // GetBookmarks is the exception to "everything is DI": the outline/bookmark tree is
    // container-level structure DI has no concept of, under any feature flag or tier. It
    // stays on PdfPig and takes an already-open PdfDocument so it can reuse the instance
    // opened earlier in the pipeline (e.g. by PdfDocumentOpener) instead of re-parsing.
    public sealed class PDFStructureExtractor
    {
        private static readonly TimeSpan[] BackoffDelays =
        {
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(13), TimeSpan.FromSeconds(34)
        };

        private readonly DocumentIntelligenceClient _diClient;
        private readonly ILogger _logger;

        public PDFStructureExtractor(DocumentIntelligenceClient diClient, ILogger logger)
        {
            _diClient = diClient;
            _logger = logger;
        }

        // The one paid call. Retries on 429 using the backoff pattern MS's own docs
        // recommend (2-5-13-34s). NB: WaitUntil.Completed polls internally, and a known
        // SDK issue (Azure/azure-sdk-for-net#50904) means that internal poll can still 429
        // independent of any DocumentIntelligenceClientOptions.Retry configured where the
        // client was constructed - this loop is why that gap doesn't just surface as an
        // unhandled exception. Any other failure returns Ok=false with a typed reason
        // instead of throwing.
        public async Task<AnalyzeOutcome> AnalyzePDFAsync(byte[] pdfBytes, string blobName, CancellationToken ct = default)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    _logger.LogInformation("Submitting '{Blob}' to Document Intelligence (attempt {Attempt}).", blobName, attempt + 1);

                    Operation<AnalyzeResult> operation = await _diClient.AnalyzeDocumentAsync(
                        WaitUntil.Completed, "prebuilt-layout", BinaryData.FromBytes(pdfBytes), cancellationToken: ct);

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
            }
        }

        // Stable content-hash for dedup/caching keys upstream - same bytes always produce
        // the same key regardless of blob name, so re-uploads of an unchanged file can be
        // recognized before paying for another analyze call.
        public static string ComputeContentHash(byte[] pdfBytes) =>
            Convert.ToHexString(SHA256.HashData(pdfBytes));

        // Headings/sections: paragraphs DI classified with a structural role (title,
        // sectionHeading, pageHeader, footnote, ...) rather than plain body text.
        public IReadOnlyList<Heading> GetHeadings(AnalyzeResult result) =>
            result.Paragraphs
                .Where(p => p.Role is not null)
                .Select(p => new Heading(p.Content, p.Role.ToString()!))
                .ToList();

        // Page geometry as DI measured it (not the PDF's declared MediaBox).
        public IReadOnlyList<PageDimensions> GetPageDimensions(AnalyzeResult result) =>
            result.Pages
                .Select(p => new PageDimensions(p.PageNumber, p.Width, p.Height, p.Unit.ToString() ?? ""))
                .ToList();

        // Tables with row/column position and cell kind (e.g. columnHeader vs content) per cell.
        public IReadOnlyList<TableInfo> GetTables(AnalyzeResult result) =>
            result.Tables
                .Select(t => new TableInfo(
                    t.RowCount,
                    t.ColumnCount,
                    t.Cells.Select(c => new TableCellInfo(c.RowIndex, c.ColumnIndex, c.Kind.ToString() ?? "", c.Content)).ToList()))
                .ToList();

        // Checkboxes/radio buttons with selected/unselected state, per page.
        public IReadOnlyList<SelectionMarkInfo> GetSelectionMarks(AnalyzeResult result) =>
            result.Pages
                .SelectMany(p => p.SelectionMarks.Select(sm => new SelectionMarkInfo(p.PageNumber, sm.State.ToString())))
                .ToList();

        // Handwritten spans at >0.8 confidence (same threshold as the original quickstart
        // sample). Styles point at spans into result.Content - they don't carry the text
        // directly, so this substrings it out.
        public IReadOnlyList<string> GetHandwrittenContent(AnalyzeResult result) =>
            result.Styles
                .Where(s => s.IsHandwritten == true && s.Confidence > 0.8)
                .SelectMany(s => s.Spans)
                .Select(span => result.Content.Substring(span.Offset, span.Length))
                .ToList();

        // Bookmarks/outline tree - PdfPig only, best-effort. No DI feature returns this
        // under any tier, but unlike everything else in this class, PdfPig's own
        // bookmark-reading code can throw PdfDocumentFormatException mid-tree-walk on a
        // malformed outline node (BookmarksProvider.ReadBookmarksRecursively) despite the
        // Try-prefixed name on TryGetBookmarks - it doesn't fully uphold the no-throw
        // contract that name implies. Bookmarks are a nice-to-have here, not a hard
        // requirement, so any failure is caught and logged rather than allowed to fail the
        // whole extraction.
        //
        // null = couldn't get bookmarks (PdfPig error) - distinct from an empty list,
        // which means extraction ran fine and this PDF genuinely has none. Callers should
        // treat null as "skip bookmarks for this document," not as a reason to fail it.
        public IReadOnlyList<Bookmark>? GetBookmarks(PdfDocument pdf, string blobName)
        {
            try
            {
                if (!pdf.TryGetBookmarks(out var bookmarks))
                {
                    _logger.LogInformation("No bookmarks/outline found in '{Blob}'.", blobName);
                    return Array.Empty<Bookmark>();
                }

                return bookmarks.GetNodes()
                    .Select(node => new Bookmark(node.Title, node.Level, TryGetPageNumber(node)))
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Bookmark extraction failed for '{Blob}'; continuing without bookmarks.", blobName);
                return null;
            }
        }

        // DocumentBookmarkNode.PageNumber resolves to a page in *this* file.
        // ExternalBookmarkNode inherits DocumentBookmarkNode (confirmed by reflecting the
        // pinned 0.1.9 assembly - it is not a sibling type) and its PageNumber is a page in
        // the *external* file it points at (see its added FileName property) - not a page
        // in this document, so it must be excluded explicitly rather than caught by the
        // DocumentBookmarkNode pattern match. PdfPig's own #736 page-number-defaults-to-0
        // fix (PR #930) isn't in 0.1.9 (that's the 0.1.9->0.1.10 diff), so an unresolvable
        // destination on this version may omit the node from the tree entirely rather than
        // reliably reporting 0 - the >0 check below is a best-effort guard for the cases
        // that do surface a value, not a substitute for that fix.
        private static int? TryGetPageNumber(BookmarkNode node) =>
            node is ExternalBookmarkNode
                ? null
                : node is DocumentBookmarkNode doc && doc.PageNumber > 0
                    ? doc.PageNumber
                    : null;
    }
}
