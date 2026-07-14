using Microsoft.Extensions.Logging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Outline;

namespace ProtocolsIndexer.Services;

// Everything a page needs to compare itself against, computed once for the whole
// document: a heading is only "big" or "known" relative to *this* document's typical
// text/layout, not to any fixed number.
internal readonly record struct PdfDocumentBaseline(
    double          DominantFontSize,
    double          DominantPageWidth,
    HashSet<string> KnownSections,
    bool            BookmarksContributed); // true if the PDF's own outline added headings beyond the hardcoded vocabulary

// Step 1 (document-level baseline) of PdfPigExtractor. Owns the known-sections
// vocabulary (hardcoded defaults + whatever's passed at construction) since bookmark
// merging is the one baseline piece that's stateful per extractor instance.
internal sealed class PdfDocumentBaselineCalculator
{
    // sample maybe 20-30 real documents across different authors/departments and check two things:
    // (1) do headings look visually distinct (bold/larger) or do the PDFs carry bookmarks, and
    // (2) do section names actually recur across documents.
    // If (1) is true, you don't need a vocabulary at all — font-size/bold/bookmark detection already carries it, and vocabulary is just a nice-to-have on top.
    // If documents are genuinely heterogeneous with no visual distinction and no bookmarks,
    // no vocabulary size will fix that — that's a different, harder problem (ML/LLM-based heading detection, not a lookup table),
    //  and the honest move is to let DocsWithNoPagesWithHeading in the validator report tell you how big that bucket actually is before investing in solving it.
    private static readonly HashSet<string> DefaultKnownSections = new(StringComparer.OrdinalIgnoreCase)
    {
    };

    private const double DefaultFontSize  = 10.0;  // matches GetDominantFontSize's own empty-input fallback
    private const double DefaultPageWidth = 612.0; // US Letter width, points

    private readonly HashSet<string> _knownSections;
    private readonly ILogger         _logger;

    public PdfDocumentBaselineCalculator(ILogger logger, IEnumerable<string>? knownSections = null)
    {
        _logger        = logger;
        _knownSections = knownSections is null
            ? new HashSet<string>(DefaultKnownSections, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(knownSections, StringComparer.OrdinalIgnoreCase);
    }

    // Baseline computation is a quality improvement, not a correctness requirement —
    // same rationale as PdfDecorationDetector/PdfMetadataTextBuilder. Each of the three
    // pieces degrades to a safe default independently, so one failing (e.g. a corrupt
    // Outlines dictionary breaking bookmark lookup) doesn't throw away the other two,
    // which may have computed fine.
    public PdfDocumentBaseline Compute(PdfDocument pdf, IReadOnlyList<Page> allPages, string blobName)
    {
        var dominantFontSize  = TryCompute(blobName, "dominant font size",       DefaultFontSize,  () => GetDominantFontSize(allPages));
        var dominantPageWidth = TryCompute(blobName, "dominant page width",      DefaultPageWidth, () => GetDominantPageWidth(allPages));
        var knownSections     = TryCompute(blobName, "known sections/bookmarks", _knownSections,   () => GetKnownSections(pdf, blobName));

        return new PdfDocumentBaseline(
            dominantFontSize, dominantPageWidth, knownSections,
            BookmarksContributed: knownSections.Count > _knownSections.Count);
    }

    private T TryCompute<T>(string blobName, string what, T fallback, Func<T> compute)
    {
        try { return compute(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not compute {What} for '{Blob}'; using fallback.", what, blobName);
            return fallback;
        }
    }

    private static double GetDominantFontSize(IReadOnlyList<Page> pages) =>
        pages
            .SelectMany(p => p.GetWords())
            .Where(w => w.Letters.Any())
            .GroupBy(w => Math.Round(w.Letters.Max(l => l.FontSize), 1))
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.Key ?? DefaultFontSize;

    private static double GetDominantPageWidth(IReadOnlyList<Page> pages) =>
        pages
            .GroupBy(p => Math.Round(p.MediaBox.Bounds.Width, 1))
            .OrderByDescending(g => g.Count())
            .First().Key;

    // Bookmarks (the PDF Outline pane) are the closest thing to an
    // authoritative heading list a PDF can carry — when the source Word doc
    // used heading styles and "create bookmarks" on export, this is the
    // actual document ToC, not a heuristic guess. Known to be unreliable
    // though (PdfPig issue #736: TryGetBookmarks can return true with zero
    // entries depending on how the PDF's Outlines dictionary was built), so
    // this only ever *adds* candidate headings on top of the hardcoded
    // list, never replaces the font-size/bold detection in PdfPageContentExtractor.
    private HashSet<string> GetKnownSections(PdfDocument pdf, string blobName)
    {
        if (!pdf.TryGetBookmarks(out var bookmarks) || bookmarks.Roots.Count == 0)
            return _knownSections;

        var fromBookmarks = CollectBookmarkTitles(bookmarks.Roots).ToList();
        if (fromBookmarks.Count == 0)
            return _knownSections;

        _logger.LogInformation(
            "PDF '{Blob}': added {Count} heading(s) from document bookmarks.", blobName, fromBookmarks.Count);

        var merged = new HashSet<string>(_knownSections, StringComparer.OrdinalIgnoreCase);
        merged.UnionWith(fromBookmarks);
        return merged;
    }

    private static IEnumerable<string> CollectBookmarkTitles(IEnumerable<BookmarkNode> nodes)
    {
        foreach (var node in nodes)
        {
            var title = node.Title?.Trim();
            if (!string.IsNullOrWhiteSpace(title) && title.Length < PdfPageContentExtractor.MaxHeadingLength)
                yield return title;

            foreach (var childTitle in CollectBookmarkTitles(node.Children))
                yield return childTitle;
        }
    }
}
