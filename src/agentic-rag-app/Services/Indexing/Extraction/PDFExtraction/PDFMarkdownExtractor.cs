using System.Text;
using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services
{
    // Assembles a Document Intelligence AnalyzeResult into markdown-formatted PDF pages.
    // Split out of PDFStructureExtractor so page/markdown assembly (this class) is
    // separate from the raw DI call and the Get* structural probes (that class).
    public sealed class PDFMarkdownExtractor
    {
        private readonly ILogger _logger;

        public PDFMarkdownExtractor(ILogger<PDFMarkdownExtractor> logger)
        {
            _logger = logger;
        }

        // Builds one PdfPageRecord per PDF page, with content formatted as markdown
        // (e.g. "## " for headings, real pipe tables with columns).
        // - This is the actual output shape used by the DocumentIntelligence backend.
        //
        // How bookmarks are used, when present:
        // - A breadcrumb line like "_Section: Chapter 3 > 3.2 Dosage_" is prepended to
        //   every page that falls under that outline entry.
        // - This breadcrumb is a stronger signal of "where am I in the document" than DI's
        //   own per-paragraph heading roles, so it supplements (does not replace) the
        //   "## " headings, which still come from DI's own Role classification.
        // - If bookmarks are missing or empty (no outline in the PDF, or PdfPig couldn't
        //   read one), pages simply get no breadcrumb - this is not treated as an error.
        public List<PdfPageRecord> BuildMarkdownPages(
            string blobName, AnalyzeResult analysis, int pageCount, IReadOnlyList<Bookmark>? bookmarks = null)
        {
            var pageSegments = new Dictionary<int, List<string>>();

            List<string> Segments(int pageNum) =>
                pageSegments.TryGetValue(pageNum, out var l) ? l : pageSegments[pageNum] = [];

            // Why this filtering step exists:
            // - DI also repeats every table cell's text as its own paragraph entry.
            // - Without filtering these out, the same table content would appear twice:
            //   once as jumbled prose (from paragraphs) and once as the markdown table below.
            // - To prevent that, first collect every span range covered by a table, then
            //   later skip any paragraph whose span falls inside one of those ranges.
            var tableSpanRanges = (analysis.Tables ?? [])
                .SelectMany(t => t.Spans ?? [])
                .Select(sp => (Start: sp.Offset, End: sp.Offset + sp.Length))
                .ToList();

            bool OverlapsTable(IReadOnlyList<DocumentSpan>? spans) =>
                spans != null && spans.Any(sp => tableSpanRanges.Any(r => sp.Offset < r.End && sp.Offset + sp.Length > r.Start));

            // Paragraph ordering:
            // - analysis.Paragraphs already comes in correct reading order, because DI
            //   resolves multi-column layouts itself.
            // - This order is intentionally kept as-is, rather than re-sorted by vertical
            //   position, which would incorrectly interleave left/right columns on
            //   two-column pages.
            // - Tables don't have a natural place in this paragraph list, so their position
            //   is instead determined by comparing their span offsets to paragraph offsets.
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
                    continue; // Skip: this text is already rendered as part of a table's markdown.

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

            var breadcrumbByPage     = BuildSectionBreadcrumbs(bookmarks, pageCount);
            var selectionBlockByPage = BuildSelectionMarkBlocks(analysis);

            // Second pass over all pages, to finalize each page's content:
            // - If a page's own content doesn't start with a heading, carry forward the
            //   most recent heading seen so far, so every page keeps a sense of "what
            //   section is this". This matters because chunking happens per page later,
            //   and a page without its own heading couldn't recover this info afterward.
            // - Then prepend the section breadcrumb (if any) and append the selection-mark
            //   block (if any).
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

                if (selectionBlockByPage.TryGetValue(pageNum, out var selectionBlock))
                    segments = [.. segments, selectionBlock];

                pages.Add(new PdfPageRecord
                {
                    BlobName    = blobName,
                    PageIndex   = pageNum,
                    PageContent = string.Join("\n\n", segments),
                });
            }

            return pages;
        }

        // Converts the PDF's flat bookmark/outline list into one breadcrumb string per page,
        // e.g. "Chapter 3 > 3.2 Dosage" - the deepest section active on that page, together
        // with all its parent sections.
        // How it works:
        // 1. Keep only bookmarks that have a resolvable page number (see
        //    PdfMetadataExtractor.TryGetPageNumber); others are skipped, since they can't be
        //    anchored to a page and would otherwise corrupt the algorithm below.
        // 2. Sort those bookmarks by page number, then walk them in order while maintaining
        //    a "stack" of section titles indexed by outline depth (Level):
        //    - Before adding a bookmark, trim the stack down to its Level, so that a new
        //      top-level bookmark correctly discards any deeper sub-sections left over from
        //      the previous chapter.
        //    - Then push the bookmark's title onto the stack.
        //    - Joining the stack with " > " gives the breadcrumb text active from that
        //      bookmark's page onward.
        // 3. Finally, walk every page number from 1 to pageCount and assign it whichever
        //    breadcrumb was most recently active as of that page.
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

        // Builds a "Selected options" markdown block per page for checkboxes/radio buttons.
        // Why this is needed:
        // - DI treats a checkbox/radio button (DocumentSelectionMark) as a non-text glyph,
        //   so it never shows up in analysis.Paragraphs.
        // - Nothing else in this class reads analysis.Pages[].SelectionMarks, so without
        //   this method, "which option was checked" would be silently lost for every page.
        // How labels are guessed:
        // - DI gives no direct link between a selection mark and the text label next to it.
        // - As a best-effort guess, this uses whichever paragraph on the same page has the
        //   closest span offset to the mark.
        // How the result is rendered:
        // - As a separate "Selected options" block appended after the page's normal content,
        //   not mixed into the paragraph text itself - so a wrong label guess can't corrupt
        //   unrelated prose.
        private static Dictionary<int, string> BuildSelectionMarkBlocks(AnalyzeResult analysis)
        {
            var result = new Dictionary<int, string>();

            var paragraphsByPage = (analysis.Paragraphs ?? [])
                .Select(p => (
                    Offset: p.Spans is { Count: > 0 } ps ? (int?)ps[0].Offset : null,
                    PageNumber: p.BoundingRegions is { Count: > 0 } br ? br[0].PageNumber : 0,
                    p.Content))
                .Where(p => p.Offset is not null && p.PageNumber > 0)
                .GroupBy(p => p.PageNumber)
                .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Offset).ToList());

            foreach (var page in analysis.Pages ?? [])
            {
                if (page.SelectionMarks is not { Count: > 0 } marks) continue;

                var lines = new List<string>();
                foreach (var mark in marks.OrderBy(m => m.Span.Offset))
                {
                    var label = "(unlabeled)";
                    if (paragraphsByPage.TryGetValue(page.PageNumber, out var candidates) && candidates.Count > 0)
                    {
                        var nearestText = candidates
                            .OrderBy(c => Math.Abs(c.Offset!.Value - mark.Span.Offset))
                            .First().Content?.Trim();

                        if (!string.IsNullOrEmpty(nearestText))
                            label = nearestText.Length > 100 ? nearestText[..100] + "…" : nearestText;
                    }

                    var box = mark.State == DocumentSelectionMarkState.Selected ? "[x]" : "[ ]";
                    lines.Add($"- {box} {label}");
                }

                result[page.PageNumber] = "**Selected options:**\n" + string.Join("\n", lines);
            }

            return result;
        }

        // Renders one DI table as a proper markdown pipe table, using its row/column indices
        // to place cells correctly (rather than just joining every cell's text in reading
        // order, which loses the table structure).
        // - Merged cells (row/column spans) are ignored: a merged cell's content is only
        //   placed in its top-left "anchor" slot. This is an accepted simplification.
        // - The table's caption and footnotes are rendered around the table rather than
        //   dropped, because a caption like "Table 3: Dosage schedule" is often the
        //   strongest retrieval signal for that table's content. These aren't duplicated
        //   elsewhere, because their spans fall inside the table's own Spans range and are
        //   already excluded from the paragraph loop in BuildMarkdownPages.
        private static string RenderMarkdownTable(DocumentTable table)
        {
            if (table.RowCount == 0 || table.ColumnCount == 0) return "";

            var grid = new string?[table.RowCount, table.ColumnCount];
            foreach (var cell in table.Cells)
                if (cell.RowIndex < table.RowCount && cell.ColumnIndex < table.ColumnCount)
                    grid[cell.RowIndex, cell.ColumnIndex] = cell.Content?.Trim();

            // Determine how many header rows there are:
            // - DI marks header cells with Kind == ColumnHeader.
            // - Count header rows starting from the top and stopping at the first
            //   non-header row - this correctly handles 0, 1, or multiple header rows.
            // - If no cell is marked as a header at all, default to treating row 0 as the
            //   header, so the output is still a valid markdown table (which requires
            //   a header row and a separator row).
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
