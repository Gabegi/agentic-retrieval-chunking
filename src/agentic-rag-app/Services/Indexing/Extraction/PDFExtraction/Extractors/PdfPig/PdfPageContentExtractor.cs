using System.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace ProtocolsIndexer.Services;

// Step 5 (per-page content + heading classification) of PdfPigExtractor. No external
// dependencies beyond PdfPig's own stateless singletons, so this stays fully static —
// nothing here needs a logger or per-instance state.
internal static class PdfPageContentExtractor
{
    // a line becomes header if format is 15% than page's dominant font size
    private const double HeadingFontSizeRatio     = 1.15;
    // same but for bold text
    private const double BoldHeadingFontSizeRatio = 1.05;
    // a line matched by size ratio is only captured if it's less than MaxHeadingLength
    internal const int MaxHeadingLength = 80; // also used by PdfDocumentBaselineCalculator's bookmark-title filter
    // A bold line only becomes a heading if it's ≤6 words
    private const int MaxBoldHeadingWords = 6;

    // Stateless PdfPig singletons, safe to share across calls.
    private static readonly NearestNeighbourWordExtractor    WordExtractor = NearestNeighbourWordExtractor.Instance;
    private static readonly UnsupervisedReadingOrderDetector ReadingOrder  = UnsupervisedReadingOrderDetector.Instance;

    // Isolated bullet points can otherwise get cut into their own spurious
    // column by RecursiveXYCut; per the wiki's own tuning note, a minimum
    // block width of ~1/3 page width avoids that without blocking genuine
    // column splits.
    public static RecursiveXYCut CreateSegmenter(double pageWidth) =>
        new(new RecursiveXYCut.RecursiveXYCutOptions { MinimumWidth = pageWidth / 3 });

    // Rebuilds reading order geometrically instead of by raw Y-coordinate:
    //   1. Words (NearestNeighbourWordExtractor) — connects glyphs by
    //      proximity, independent of the order the PDF drew them in.
    //   2. Blocks (segmenter, shared across the document — see PdfPigExtractor.ExtractPDF) —
    //      cuts on column gaps, so a 2-column page yields separate column
    //      blocks instead of one interleaved mess.
    //   3. Reading order (UnsupervisedReadingOrderDetector) — walks blocks
    //      the way a person reads them: column A fully, then column B.
    // Heading classification runs per TextLine within each ordered block,
    // using the same known-vocabulary / font-size / bold signals as before.
    // Tables aren't extracted separately here — a table's cells get read as
    // ordinary text lines, in reading order, same as everything else on the
    // page (see conversation/decision to drop Tabula-based extraction).
    public static string ExtractPageContent(
        Page page, RecursiveXYCut segmenter, double dominantFontSize, HashSet<string> decoration, HashSet<string> knownSections,
        ref string? currentHeading, out bool pageHadHeading)
    {
        var buffer     = new StringBuilder();
        var hadHeading = false;

        var words   = WordExtractor.GetWords(page.Letters);
        var blocks  = segmenter.GetBlocks(words);
        var ordered = ReadingOrder.Get(blocks);

        foreach (var block in ordered)
        {
            if (decoration.Contains(block.Text.Trim())) continue; // running header/footer

            foreach (var line in block.TextLines)
            {
                var text = string.Join(" ", line.Words.Select(w => w.Text)).Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;

                var letters  = line.Words.SelectMany(w => w.Letters).ToList();
                var fontSize = letters.Count > 0 ? letters.Max(l => l.FontSize) : dominantFontSize;
                var isBold   = letters.Any(l => l.FontName?.Contains("Bold", StringComparison.OrdinalIgnoreCase) == true);

                var isKnown     = knownSections.Contains(text);
                var isFont      = fontSize > dominantFontSize * HeadingFontSizeRatio && text.Length < MaxHeadingLength;
                var isBoldShort = isBold && fontSize > dominantFontSize * BoldHeadingFontSizeRatio
                                  && text.Split(' ').Length <= MaxBoldHeadingWords;

                if (isKnown || isFont || isBoldShort)
                {
                    currentHeading = text;
                    hadHeading     = true;
                    buffer.Append(buffer.Length > 0 ? "\n\n" : "").Append("## ").Append(text);
                }
                else
                {
                    buffer.Append(buffer.Length > 0 ? " " : "").Append(text);
                }
            }
        }

        pageHadHeading = hadHeading;
        return buffer.ToString().Trim();
    }
}
