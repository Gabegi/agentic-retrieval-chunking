using System.Text;
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
    // Everything that reads structure out of a Document Intelligence AnalyzeResult (plus
    // PdfPig's bookmark tree) for the DocumentIntelligence backend - the paid analyze
    // call, markdown page assembly, and the DI-vs-PdfPig comparison's capability probes.
    // AnalyzePDFAsync is the only paid call (with retry on 429); everything else is a
    // free, synchronous read of the resulting AnalyzeResult. Call AnalyzePDFAsync once per
    // document, then feed its result into BuildMarkdownPages and/or as many Get* methods
    // as you need.
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

        // Assembles one PdfPageRecord per PDF page with markdown-flavored content
        // ("## " headings, real column-aware pipe tables) - the DocumentIntelligence
        // backend's actual output shape, as opposed to the Get* probes below, which exist
        // for the DI-vs-PdfPig capability comparison and deliberately drop the
        // page-number/span-offset info this method needs to place content in order.
        //
        // bookmarks, when available, prepend a section breadcrumb ("_Section: Chapter 3 >
        // 3.2 Dosage_") to every page under that outline entry - a stronger signal of a
        // page's place in the document than DI's own per-paragraph heading roles, which
        // this breadcrumb supplements rather than replaces (the "## " headings below still
        // come from DI's Role classification). null/empty bookmarks (no outline in this
        // PDF, or PdfPig failed to read one) just means no breadcrumb - never a hard
        // requirement for extraction to succeed.
        public List<PdfPageRecord> BuildMarkdownPages(
            string blobName, AnalyzeResult analysis, int pageCount, IReadOnlyList<Bookmark>? bookmarks = null)
        {
            var pageSegments = new Dictionary<int, List<string>>();

            List<string> Segments(int pageNum) =>
                pageSegments.TryGetValue(pageNum, out var l) ? l : pageSegments[pageNum] = [];

            // DI repeats every table cell's text as its own entry in analysis.Paragraphs,
            // so without filtering, table content is emitted twice: once as jumbled prose,
            // once as the markdown table below. Build the set of spans covered by tables
            // up front and drop any paragraph whose span falls inside one.
            var tableSpanRanges = (analysis.Tables ?? [])
                .SelectMany(t => t.Spans ?? [])
                .Select(sp => (Start: sp.Offset, End: sp.Offset + sp.Length))
                .ToList();

            bool OverlapsTable(IReadOnlyList<DocumentSpan>? spans) =>
                spans != null && spans.Any(sp => tableSpanRanges.Any(r => sp.Offset < r.End && sp.Offset + sp.Length > r.Start));

            // analysis.Paragraphs is already in reading order -- DI resolves multi-column
            // layouts etc. -- so it's kept as-is rather than re-sorted by Y, which would
            // interleave left/right columns on two-column pages. Tables are positioned by
            // comparing span offsets against paragraph offsets instead.
            var tablesByOffset = (analysis.Tables ?? [])
                .Select(t => (Table: t, Offset: t.Spans is { Count: > 0 } ts ? ts[0].Offset : 0))
                .OrderBy(t => t.Offset)
                .ToList();
            var nextTableIndex = 0;

            void EmitTable(DocumentTable table)
            {
                var pageNum = table.BoundingRegions is { Count: > 0 } br ? br[0].PageNumber : 0;
                if (pageNum == 0)
                {
                    _logger.LogWarning("DocumentIntelligence: table with no bounding region dropped in {BlobName}", blobName);
                    return;
                }

                var markdown = RenderMarkdownTable(table);
                if (!string.IsNullOrWhiteSpace(markdown))
                    Segments(pageNum).Add(markdown);
            }

            foreach (var para in analysis.Paragraphs ?? [])
            {
                if (para.Role == ParagraphRole.PageHeader
                    || para.Role == ParagraphRole.PageFooter
                    || para.Role == ParagraphRole.PageNumber)
                    continue;

                if (OverlapsTable(para.Spans))
                    continue; // already rendered as part of its table's markdown

                var paraOffset = para.Spans is { Count: > 0 } ps ? ps[0].Offset : 0;
                while (nextTableIndex < tablesByOffset.Count && tablesByOffset[nextTableIndex].Offset < paraOffset)
                    EmitTable(tablesByOffset[nextTableIndex++].Table);

                var pageNum = para.BoundingRegions is { Count: > 0 } brP ? brP[0].PageNumber : 0;
                if (pageNum == 0)
                {
                    _logger.LogWarning("DocumentIntelligence: paragraph with no bounding region dropped in {BlobName}", blobName);
                    continue;
                }

                if (para.Role == ParagraphRole.Title)
                    Segments(pageNum).Add($"# {para.Content}");
                else if (para.Role == ParagraphRole.SectionHeading)
                    Segments(pageNum).Add($"## {para.Content}");
                else
                    Segments(pageNum).Add(para.Content);
            }

            while (nextTableIndex < tablesByOffset.Count)
                EmitTable(tablesByOffset[nextTableIndex++].Table);

            var breadcrumbByPage = BuildSectionBreadcrumbs(bookmarks, pageCount);

            // Second pass: carry the most recent heading into a page whose own content
            // doesn't start with one, so every page still carries its section identity —
            // chunking happens per-page downstream, so this can't be recovered later.
            var pages        = new List<PdfPageRecord>();
            string? carryHeading = null;

            static bool IsHeading(string s) => s.StartsWith("# ") || s.StartsWith("## ");

            for (var pageNum = 1; pageNum <= pageCount; pageNum++)
            {
                var segments = pageSegments.TryGetValue(pageNum, out var s) ? s : [];

                if (segments.Count > 0 && !IsHeading(segments[0]) && carryHeading != null)
                    segments = [carryHeading, .. segments];

                var lastHeadingOnPage = segments.LastOrDefault(IsHeading);
                if (lastHeadingOnPage != null) carryHeading = lastHeadingOnPage;

                if (breadcrumbByPage.TryGetValue(pageNum, out var breadcrumb))
                    segments = [breadcrumb, .. segments];

                pages.Add(new PdfPageRecord
                {
                    BlobName    = blobName,
                    PageIndex   = pageNum,
                    PageContent = string.Join("\n\n", segments),
                });
            }

            return pages;
        }

        // Reduces the flat, pre-order bookmark list into one breadcrumb string per page -
        // the deepest outline entry active as of that page, joined with its ancestors
        // ("Chapter 3 > 3.2 Dosage"). Maintains a level-indexed stack while walking
        // bookmarks in page order: each entry truncates the stack to its own Level before
        // pushing, so a later top-level bookmark correctly drops any deeper section left
        // over from the previous chapter. Entries with no resolvable PageNumber (see
        // PDFStructureExtractor.TryGetPageNumber) are skipped - they can't anchor a page
        // range and would otherwise corrupt the stack with an untethered entry.
        private static Dictionary<int, string> BuildSectionBreadcrumbs(IReadOnlyList<Bookmark>? bookmarks, int pageCount)
        {
            var result = new Dictionary<int, string>();
            if (bookmarks is not { Count: > 0 }) return result;

            var ordered = bookmarks
                .Where(b => b.PageNumber is > 0)
                .OrderBy(b => b.PageNumber)
                .ToList();
            if (ordered.Count == 0) return result;

            var stack = new List<string>();
            var breakpoints = new List<(int PageNumber, string Path)>();

            foreach (var bm in ordered)
            {
                if (stack.Count > bm.Level) stack.RemoveRange(bm.Level, stack.Count - bm.Level);
                while (stack.Count < bm.Level) stack.Add("");
                stack.Add(bm.Title);

                breakpoints.Add((bm.PageNumber!.Value, string.Join(" > ", stack)));
            }

            var breakpointIndex = 0;
            string? current = null;
            for (var pageNum = 1; pageNum <= pageCount; pageNum++)
            {
                while (breakpointIndex < breakpoints.Count && breakpoints[breakpointIndex].PageNumber <= pageNum)
                    current = breakpoints[breakpointIndex++].Path;

                if (current != null)
                    result[pageNum] = $"_Section: {current}_";
            }

            return result;
        }

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

        // Column-aware markdown table rendering using Document Intelligence's row/column
        // indices — a real pipe table with a header row, unlike the comparison spike's
        // flat " | " join of every cell in reading order. Row/column spans are ignored:
        // a merged cell fills only its anchor slot, which is acceptable for this use.
        // Caption/footnotes are rendered around the table rather than dropped — a table's
        // caption ("Table 3: Dosage schedule") is often the strongest retrieval signal for
        // that table's content, and their spans fall inside the table's own Spans range, so
        // they're already excluded from the paragraph loop above (no duplicate emission).
        private static string RenderMarkdownTable(DocumentTable table)
        {
            if (table.RowCount == 0 || table.ColumnCount == 0) return "";

            var grid = new string?[table.RowCount, table.ColumnCount];
            foreach (var cell in table.Cells)
                if (cell.RowIndex < table.RowCount && cell.ColumnIndex < table.ColumnCount)
                    grid[cell.RowIndex, cell.ColumnIndex] = cell.Content?.Trim();

            // DI marks header cells with Kind == ColumnHeader; header rows are assumed
            // contiguous from the top (covers 0, 1, or multi-row headers). When no cell
            // carries that kind at all, fall back to treating row 0 as the header so the
            // output stays a valid markdown table.
            var headerRowCount = 0;
            while (headerRowCount < table.RowCount
                   && table.Cells.Any(c => c.RowIndex == headerRowCount && c.Kind == DocumentTableCellKind.ColumnHeader))
                headerRowCount++;
            if (headerRowCount == 0) headerRowCount = 1;

            var sb = new StringBuilder();

            var caption = table.Caption?.Content?.Trim();
            if (!string.IsNullOrEmpty(caption))
                sb.Append("**").Append(caption).Append("**\n\n");

            for (var r = 0; r < table.RowCount; r++)
            {
                sb.Append('|');
                for (var c = 0; c < table.ColumnCount; c++)
                    sb.Append(' ').Append(grid[r, c] ?? "").Append(" |");
                sb.Append('\n');

                if (r == headerRowCount - 1)
                {
                    sb.Append('|');
                    for (var c = 0; c < table.ColumnCount; c++)
                        sb.Append(" --- |");
                    sb.Append('\n');
                }
            }

            foreach (var footnote in table.Footnotes ?? [])
            {
                var text = footnote.Content?.Trim();
                if (!string.IsNullOrEmpty(text))
                    sb.Append("\n_").Append(text).Append("_\n");
            }

            return sb.ToString().TrimEnd();
        }
    }
}
