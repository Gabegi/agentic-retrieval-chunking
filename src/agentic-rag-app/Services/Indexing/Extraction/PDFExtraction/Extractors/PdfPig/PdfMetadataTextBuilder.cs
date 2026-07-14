using Microsoft.Extensions.Logging;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace ProtocolsIndexer.Services;

// 
internal sealed class RawTextExtractor
{
    private static readonly NearestNeighbourWordExtractor    WordExtractor = NearestNeighbourWordExtractor.Instance;
    private static readonly UnsupervisedReadingOrderDetector ReadingOrder  = UnsupervisedReadingOrderDetector.Instance;

    private readonly ILogger _logger;

    public RawTextExtractor(ILogger logger)
    {
        _logger = logger;
    }

    public string Extract(IEnumerable<Page> pages, RecursiveXYCut segmenter)
    {
        try
        {
            var pageTexts = pages.Select(page =>
            {
                //  groups a page's individual glyphs (letters) into words based on proximity
                var words   = WordExtractor.GetWords(page.Letters);
                var blocks  = segmenter.GetBlocks(words);
                var ordered = ReadingOrder.Get(blocks);

                var lines = ordered.SelectMany(b => b.TextLines)
                    .Select(l => string.Join(" ", l.Words.Select(w => w.Text)).Trim())
                    .Where(l => l.Length > 0);

                return string.Join("\n", lines);
            });

            return string.Join("\n\n", pageTexts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not build metadata text; title/version extraction may fall back to defaults.");
            return "";
        }
    }
}
