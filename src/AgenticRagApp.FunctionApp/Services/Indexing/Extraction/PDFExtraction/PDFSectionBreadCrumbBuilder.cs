using AgenticRagApp.Models;

namespace AgenticRagApp.Services;

// Converts a PDF's flat bookmark/outline list into one breadcrumb string per page, e.g.
// "Chapter 3 > 3.2 Dosage" - the deepest section active on that page, plus its parents.
// Extracted out of the deleted PDFMarkdownExtractor: real PDF outline data and a correct
// stack algorithm are worth keeping for a future chunk-builder to attach per-chunk (not
// per-page, the way the old class did it).
public static class PdfSectionBreadCrumbBuilder
{
    // Steps:
    // 1. Keep only bookmarks with a resolvable page number - others can't be anchored to
    //    a page and would corrupt the walk below. Diagnostics: split into external-file
    //    links vs. unresolvable internal destinations, since both collapse to
    //    PageNumber=null but mean different things to a report reader.
    // 2. Sort by page number, then walk in order maintaining a "stack" of titles indexed
    //    by outline depth (Level):
    //    - Trim the stack to Level before adding, so a new top-level bookmark discards
    //      any deeper sub-sections left over from the previous chapter.
    //    - Push the bookmark's title; join the stack's non-empty entries with " > ".
    //    - A skipped outline level (e.g. Level 2 directly under Level 0) stays blank and
    //      gets filtered out, so it renders as "Chapter 3 > 3.2.1", not "Chapter 3 >  > 3.2.1"
    //      - also counted as a diagnostic, since it usually means a sloppily-authored outline.
    // 3. Walk every page 1..pageCount, assigning whichever breadcrumb was most recently
    //    active as of that page.
    //
    // diagnostics is report/diagnostic material only (see PdfStepDiagnostics) - this
    // method never fails; a bad/sparse outline just produces fewer breadcrumbs.
    public static (Dictionary<int, string> Breadcrumbs, PdfStepDiagnostics Diagnostics) BuildSectionBreadcrumbs(
        IReadOnlyList<Bookmark>? bookmarks, int pageCount, string blobName)
    {
        var result   = new Dictionary<int, string>();
        var warnings = new List<ExtractionWarning>();

        if (bookmarks is not { Count: > 0 })
            return (result, new PdfStepDiagnostics(warnings, []));

        var unresolvable         = bookmarks.Where(b => b.PageNumber is not > 0).ToList();
        var externalCount        = unresolvable.Count(b => b.IsExternal);
        var unresolvableInternal = unresolvable.Count - externalCount;

        if (unresolvableInternal > 0)
            warnings.Add(new ExtractionWarning
            {
                DocumentId = blobName,
                Message    = $"{unresolvableInternal} bookmark(s) skipped - no resolvable page.",
            });

        if (externalCount > 0)
            warnings.Add(new ExtractionWarning
            {
                DocumentId = blobName,
                Message    = $"{externalCount} bookmark(s) excluded - point to an external file, not a page in this document.",
            });

        // Stable sort matters here: two bookmarks on the same page keep their original
        // outline order (OrderBy is guaranteed stable in .NET) - if it weren't, a page
        // with multiple bookmarks could end up with the wrong one "most recently active".
        var ordered = bookmarks
            .Where(b => b.PageNumber is > 0)
            .OrderBy(b => b.PageNumber)
            .ToList();

        if (ordered.Count == 0)
            return (result, new PdfStepDiagnostics(warnings, []));

        var outOfRange = ordered.Count(b => b.PageNumber > pageCount);
        if (outOfRange > 0)
            warnings.Add(new ExtractionWarning
            {
                DocumentId = blobName,
                Message    = $"{outOfRange} bookmark(s) point beyond this document's {pageCount} page(s) - never assigned to a breadcrumb.",
            });

        var stack           = new List<string>();
        var breakpoints      = new List<(int PageNumber, string Path)>();
        var skippedLevelGaps = 0;

        foreach (var bm in ordered)
        {
            if (bm.Level > stack.Count) skippedLevelGaps++;

            if (stack.Count > bm.Level) stack.RemoveRange(bm.Level, stack.Count - bm.Level);
            while (stack.Count < bm.Level) stack.Add("");
            stack.Add(bm.Title);

            breakpoints.Add((bm.PageNumber!.Value, string.Join(" > ", stack.Where(s => s.Length > 0))));
        }

        if (skippedLevelGaps > 0)
            warnings.Add(new ExtractionWarning
            {
                DocumentId = blobName,
                Message    = $"{skippedLevelGaps} bookmark(s) skip an outline level (e.g. Level 2 directly under Level 0) - sloppy outline structure.",
            });

        var breakpointIndex = 0;
        string? current = null;
        for (var pageNum = 1; pageNum <= pageCount; pageNum++)
        {
            while (breakpointIndex < breakpoints.Count && breakpoints[breakpointIndex].PageNumber <= pageNum)
                current = breakpoints[breakpointIndex++].Path;

            if (current != null)
                result[pageNum] = $"_Section: {current}_";
        }

        if (result.Count == 0)
            warnings.Add(new ExtractionWarning
            {
                DocumentId = blobName,
                Message    = $"{ordered.Count} bookmark(s) existed but none resolved to a breadcrumb on any page.",
            });

        return (result, new PdfStepDiagnostics(warnings, []));
    }
}
