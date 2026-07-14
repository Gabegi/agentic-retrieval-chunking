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

    public string ExtractNOrganiseText(IEnumerable<Page> pages, RecursiveXYCut segmenter)
    {
        try
        {   // words → blocks → reading order
            var pageTexts = pages.Select(page =>
            {

                //  groups nearby letters into Word objects
                // groups a page's individual glyphs (letters) into words based on proximity
                var words   = WordExtractor.GetWords(page.Letters);

                // groups Word objects spatially into Blocks
                // groups words into rectangular regions by finding whitespace gaps between them
                var blocks  = segmenter.GetBlocks(words);
                
                // reorders those Block objects
                // sequences unordered blocks into the order a human would actually read them
                var ordered = ReadingOrder.Get(blocks);

                // puts everything together
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
