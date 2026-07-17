using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// Converts a PDF's flat bookmark/outline list into one breadcrumb string per page, e.g.
// "Chapter 3 > 3.2 Dosage" - the deepest section active on that page, plus its parents.
// Extracted out of the deleted PDFMarkdownExtractor: real PDF outline data and a correct
// stack algorithm are worth keeping for a future chunk-builder to attach per-chunk (not
// per-page, the way the old class did it).
public static class PDFSectionBreadCrumbBuilder
{
    // Steps:
    // 1. Keep only bookmarks with a resolvable page number - others can't be anchored to
    //    a page and would corrupt the walk below.
    // 2. Sort by page number, then walk in order maintaining a "stack" of titles indexed
    //    by outline depth (Level):
    //    - Trim the stack to Level before adding, so a new top-level bookmark discards
    //      any deeper sub-sections left over from the previous chapter.
    //    - Push the bookmark's title; join the stack's non-empty entries with " > ".
    //    - A skipped outline level (e.g. Level 2 directly under Level 0) stays blank and
    //      gets filtered out, so it renders as "Chapter 3 > 3.2.1", not "Chapter 3 >  > 3.2.1".
    // 3. Walk every page 1..pageCount, assigning whichever breadcrumb was most recently
    //    active as of that page.
    public static Dictionary<int, string> BuildSectionBreadcrumbs(IReadOnlyList<Bookmark>? bookmarks, int pageCount)
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
