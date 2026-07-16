using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;

namespace ProtocolsIndexer.Services;

// Builds the RecursiveXYCut block-segmenter used throughout PdfPigExtractor's pipeline.
// Kept separate from PdfPageContentExtractor since the one segmenter instance this
// produces is shared across three different consumers (PdfDecorationDetector,
// PdfMetadataTextBuilder, PdfPageContentExtractor itself) — it's a document-wide tool
// configured from the baseline, not something owned by any one of them.
internal static class PdfSegmenterFactory
{
    // Isolated bullet points can otherwise get cut into their own spurious
    // column by RecursiveXYCut; per the wiki's own tuning note, a minimum
    // block width of ~1/3 page width avoids that without blocking genuine
    // column splits.
    public static RecursiveXYCut CreateSegmenter(double pageWidth) =>
        new(new RecursiveXYCut.RecursiveXYCutOptions { MinimumWidth = pageWidth / 3 });
}
