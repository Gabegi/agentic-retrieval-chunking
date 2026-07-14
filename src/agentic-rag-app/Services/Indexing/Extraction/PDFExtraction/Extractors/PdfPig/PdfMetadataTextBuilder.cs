using Microsoft.Extensions.Logging;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace ProtocolsIndexer.Services;

// Step 3 (document metadata) of PdfPigExtractor: builds the line-preserving text that
// PdfMetadataExtraction.Parse regex-matches title/version/date out of. Built separately
// from ExtractPageContent's body text on purpose: PdfMetadataExtraction.TitleRegex is
// ^(.+?)\s*\|\s*LCI-richtlijn with RegexOptions.Multiline, which needs the title
// sitting on its own physical line to match ^. PageContent joins lines within a block
// with plain spaces (see PdfPageContentExtractor), which would merge a title into its
// surrounding paragraph and silently break that anchor — title extraction would fall
// back to the filename guess with no error. This keeps one output line per PDF line
// instead, using the same word/block/reading-order pipeline as the rest of the file
// (more robust than the original spike's raw Y-grouping on a multi-column first page,
// same one-line-per-line property that regex needs).
internal sealed class PdfMetadataTextBuilder
{
    // Stateless PdfPig singletons, safe to share across calls.
    private static readonly NearestNeighbourWordExtractor    WordExtractor = NearestNeighbourWordExtractor.Instance;
    private static readonly UnsupervisedReadingOrderDetector ReadingOrder  = UnsupervisedReadingOrderDetector.Instance;

    private readonly ILogger _logger;

    public PdfMetadataTextBuilder(ILogger logger)
    {
        _logger = logger;
    }

    public string Build(IEnumerable<Page> pages, RecursiveXYCut segmenter)
    {
        try
        {
            var pageTexts = pages.Select(page =>
            {
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
