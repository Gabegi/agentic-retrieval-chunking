using System.Text.RegularExpressions;
using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services
{
    // Assembles a Document Intelligence AnalyzeResult into per-page markdown.
    //
    // ExtractPdfStructureAsync requests OutputContentFormat.Markdown, so analysis.Content
    // already arrives as one markdown-flavored string for the whole document: ATX headings
    // ("#" through "######", plus a setext "===" underline for the document title), HTML
    // tables with real rowspan/colspan for merged cells, inline "☒"/"☐" selection marks in
    // context, and "<!-- PageBreak -->" / "<!-- PageHeader=\"...\" -->" /
    // "<!-- PageFooter=\"...\" -->" / "<!-- PageNumber=\"...\" -->" comments marking page
    // boundaries and repeated boilerplate. This class's job is just to split that one
    // string back into per-page PdfPageRecords and layer on the two things DI has no way
    // to produce itself: bookmark breadcrumbs and cross-page heading carry-forward.
    public sealed class PDFMarkdownExtractor
    {
        private readonly ILogger _logger;

        public PDFMarkdownExtractor(ILogger<PDFMarkdownExtractor> logger)
        {
            _logger = logger;
        }

        private static readonly Regex PageBreakRegex =
            new(@"<!--\s*PageBreak\s*-->", RegexOptions.Compiled);

        // Matches a whole "<!-- PageHeader="..." -->"-style line (also PageFooter/
        // PageNumber/FigureContent) - DI repeats these verbatim on every page they apply
        // to, so left in place every page's chunk would pick up the same boilerplate.
        // Anchored to a full line (not "anywhere in the string") so a document that
        // happens to contain this literal text in its own prose isn't eaten by accident.
        // The quoted value is matched via (?:[^"\\]|\\.)* rather than a lazy ".*?", so an
        // escaped quote inside the captured text doesn't truncate the match early.
        private static readonly Regex NoiseCommentLineRegex = new(
            @"^[ \t]*<!--\s*(?:Page(?:Header|Footer|Number)|FigureContent)\s*=\s*""(?:[^""\\]|\\.)*""\s*-->[ \t]*\r?\n?",
            RegexOptions.Multiline | RegexOptions.Compiled);

        // DI renders the document Title as a setext heading ("Title text" on one line,
        // "===" underneath) rather than ATX "# ". Normalized to ATX up front so every
        // later heading-detection pass (HeadingLineRegex, carry-forward) only has one
        // syntax to recognize. Scoped to "=" underlines only - "-" underlines are
        // ambiguous with a markdown thematic break (<hr>), so those are deliberately left
        // alone rather than risk mis-normalizing an actual rule into a heading.
        private static readonly Regex SetextTitleRegex = new(
            @"^(?<title>[^\n]+)\r?\n=+[ \t]*$", RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex HeadingLineRegex =
            new(@"^#{1,6}[ \t]+\S.*$", RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex TableOpenTagRegex  = new(@"<table\b",     RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TableCloseTagRegex = new(@"</table\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Builds one PdfPageRecord per PDF page from DI's native markdown output.
        //
        // How bookmarks are used, when present:
        // - A breadcrumb line like "_Section: Chapter 3 > 3.2 Dosage_" is prepended to
        //   every page that falls under that outline entry - a stronger signal of a page's
        //   place in the document than any heading DI itself renders, so this supplements
        //   (does not replace) the document's own headings.
        // - Missing/empty bookmarks (no outline in this PDF, or PdfPig couldn't read one)
        //   just means no breadcrumb - never a hard requirement for extraction to succeed.
        public List<PdfPageRecord> BuildMarkdownPages(
            string blobName, AnalyzeResult analysis, int pageCount, string title, IReadOnlyList<Bookmark>? bookmarks = null)
        {
            var content = SetextTitleRegex.Replace(analysis.Content ?? "", "# ${title}");

            // Regex.Split both breaks the string apart on DI's page-boundary marker AND
            // drops the delimiter itself, so no separate "strip PageBreak" step is needed.
            var rawFragments = PageBreakRegex.Split(content);

            // Must be checked here, against the untouched split - once fragments get
            // merged below to repair a table sliced across a page break, fragments.Count
            // legitimately stops matching pageCount, and that's expected, not a sign
            // something is wrong. Checked before merging, a mismatch here means DI itself
            // merged or dropped a page, so breadcrumb page-alignment can't be trusted.
            var trustFragmentToPageMapping = rawFragments.Length == pageCount;
            if (!trustFragmentToPageMapping)
                _logger.LogWarning(
                    "DocumentIntelligence: page-break count ({Fragments}) doesn't match PageCount ({PageCount}) for {BlobName}; skipping section breadcrumbs.",
                    rawFragments.Length, pageCount, blobName);

            // Repair fragments that DI's own page-break marker sliced through the middle
            // of an HTML <table>...</table> - naive per-page splitting would otherwise hand
            // downstream two fragments of broken table markup instead of one intact table.
            // Merged content is attributed to the first page in the group: the table still
            // ends up "misplaced" onto one page rather than split across two, which is no
            // worse than this class's previous offset-based placement heuristic.
            var mergedFragments             = new List<string>();
            var mergedFragmentFirstPageIndex = new List<int>();
            var pendingOpenTables = 0;
            var groupStartIndex   = 0;

            for (var i = 0; i < rawFragments.Length; i++)
            {
                if (pendingOpenTables == 0)
                    groupStartIndex = i;

                pendingOpenTables += TableOpenTagRegex.Matches(rawFragments[i]).Count;
                pendingOpenTables -= TableCloseTagRegex.Matches(rawFragments[i]).Count;
                if (pendingOpenTables < 0) pendingOpenTables = 0; // malformed input guard - never trust a negative balance

                if (pendingOpenTables == 0)
                {
                    mergedFragments.Add(string.Concat(rawFragments[groupStartIndex..(i + 1)]));
                    mergedFragmentFirstPageIndex.Add(groupStartIndex);
                }
            }
            if (pendingOpenTables != 0) // unterminated table at end-of-document: emit what's left rather than drop it
            {
                mergedFragments.Add(string.Concat(rawFragments[groupStartIndex..]));
                mergedFragmentFirstPageIndex.Add(groupStartIndex);
            }

            var breadcrumbByPage = trustFragmentToPageMapping
                ? BuildSectionBreadcrumbs(bookmarks, pageCount)
                : new Dictionary<int, string>();

            // Populated only for the first page of each merged group - a later page
            // swallowed into an earlier page's table-repair merge simply carries no
            // content of its own, since its content already went out under the earlier page.
            var pageContentByPage = new Dictionary<int, string>();
            for (var g = 0; g < mergedFragments.Count; g++)
            {
                var pageNum = mergedFragmentFirstPageIndex[g] + 1; // fragments are 0-based, pages are 1-based
                if (pageNum > pageCount) break; // guard against a pathological split producing more groups than pages

                pageContentByPage[pageNum] = NoiseCommentLineRegex.Replace(mergedFragments[g], "").Trim('\r', '\n');
            }

            var pages = new List<PdfPageRecord>();
            string? carryHeading = null;

            for (var pageNum = 1; pageNum <= pageCount; pageNum++)
            {
                var pageContent = pageContentByPage.TryGetValue(pageNum, out var pc) ? pc : "";

                var headingsOnPage   = HeadingLineRegex.Matches(pageContent);
                var startsWithHeading = pageContent.TrimStart().StartsWith('#');

                var segments = new List<string>();
                if (!startsWithHeading && carryHeading != null)
                    segments.Add(carryHeading);
                if (pageContent.Length > 0)
                    segments.Add(pageContent);

                if (headingsOnPage.Count > 0)
                    carryHeading = headingsOnPage[^1].Value;

                if (breadcrumbByPage.TryGetValue(pageNum, out var breadcrumb))
                    segments.Insert(0, breadcrumb);

                pages.Add(new PdfPageRecord
                {
                    BlobName    = blobName,
                    PageIndex   = pageNum,
                    PageContent = string.Join("\n\n", segments),
                    Title       = title,
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
        //    - Joining the stack's non-empty entries with " > " gives the breadcrumb text
        //      active from that bookmark's page onward. An entry stays blank when a level
        //      was skipped in the outline (e.g. a level-2 bookmark appearing directly under
        //      a level-0 one) - filtered out here rather than joined, so a skipped level
        //      renders as "Chapter 3 > 3.2.1", not "Chapter 3 >  > 3.2.1".
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

                breakpoints.Add((bm.PageNumber!.Value, string.Join(" > ", stack.Where(s => s.Length > 0))));
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
    }
}
