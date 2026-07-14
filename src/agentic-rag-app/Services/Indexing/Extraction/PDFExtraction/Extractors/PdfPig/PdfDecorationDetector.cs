using Microsoft.Extensions.Logging;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace ProtocolsIndexer.Services;

// Step 2 (cross-page structural analysis) of PdfPigExtractor: finds blocks repeated
// near-verbatim across pages (running titles, page numbers, confidentiality notices)
// via the same word/segment pipeline used for content extraction, then hands back one
// decoration set per page. Matched by trimmed block text rather than object reference,
// since content extraction re-segments each page independently — the two passes agree
// because they're handed the exact same segmenter instance, not just an
// equivalently-configured one.
internal sealed class PdfDecorationDetector
{
    // Stateless PdfPig singleton, safe to share across calls.
    private static readonly NearestNeighbourWordExtractor WordExtractor = NearestNeighbourWordExtractor.Instance;

    private readonly ILogger _logger;

    public PdfDecorationDetector(ILogger logger)
    {
        _logger = logger;
    }

    public Dictionary<int, HashSet<string>> GetDecorationTextByPage(
        IReadOnlyList<Page> allPages, RecursiveXYCut segmenter, string blobName)
    {
        var result = new Dictionary<int, HashSet<string>>();
        try
        {
            var perPage = DecorationTextBlockClassifier.Get(allPages, WordExtractor, segmenter);

            for (var i = 0; i < perPage.Count; i++)
                result[i + 1] = perPage[i].Select(b => b.Text.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // Decoration detection is a quality improvement, not a
            // correctness requirement — degrade to "no header/footer
            // stripping" instead of failing the whole file over it.
            _logger.LogWarning(ex, "Decoration (header/footer) detection failed for '{Blob}'; continuing without it.", blobName);
        }
        return result;
    }
}
