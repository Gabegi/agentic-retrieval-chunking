using Azure.AI.DocumentIntelligence;

namespace ProtocolsIndexer.Models
{
    // Return types used by PDFDocumentAnalyzer's Get* methods:
    // - Each record below matches one Get* method one-to-one.
    // - This keeps callers focused only on the fields they actually asked for.
    // - Every Offset field below (Heading, TableInfo, SelectionMarkInfo, FigureInfo,
    //   LineInfo) indexes into analysis.Content / RawContent. Because
    //   AnalyzeDocumentAsync requests OutputContentFormat.Markdown, that string IS the
    //   markdown-rendered content, not plain text - DI recomputes every span against
    //   whichever format was requested, so this isn't an edge case to guard against, it's
    //   how these offsets work now. A future ChunkMetadata builder must match content
    //   against these markdown-relative offsets, not plain-text ones.
    // - Heading/TableInfo/FigureInfo/LineInfo's Offset is nullable: it's an anchor into
    //   the first Span/BoundingRegion only, and when DI didn't provide one, null means
    //   "unknown" - never 0, since 0 is itself a legitimately valid offset (the very start
    //   of the content) and couldn't otherwise be told apart from "no span data". Selection
    //   marks don't have this ambiguity (DI always gives exactly one Span per mark).

    // A single heading/boilerplate paragraph detected in the PDF:
    // - PageNumber = which page the paragraph is on, for display/debugging only.
    //   It can't be used for ordering, because two on the same page look identical by page number.
    public sealed record Heading(string Content, string Role, int? Offset, int PageNumber);

    // Paired with LineInfo for a future highlight-on-source feature (out of the embedding
    // path, in the RAG system): DI's polygons are in page units (inches, for PDFs), so
    // rendering an overlay box means normalizing LineInfo.Polygon against this page's
    // Width/Height first - a raw polygon alone isn't renderable without it.
    public sealed record PageDimensions(int PageNumber, double? Width, double? Height, string Unit);

    // RowSpan/ColumnSpan are null for a regular single-cell entry and only set on a cell
    // that merges multiple rows/columns - without them, a merged header cell looks like a
    // missing cell to anything reconstructing the table layout downstream.
    public sealed record TableCellInfo(int RowIndex, int ColumnIndex, string Kind, string Content, int? RowSpan, int? ColumnSpan);

    public sealed record TableInfo(int RowCount, int ColumnCount, IReadOnlyList<TableCellInfo> Cells, int? Offset, int PageNumber);

    // Confidence/Polygon come straight off the same DocumentSelectionMark GetSelectionMarks
    // already iterates for State/Offset - free fields on an object already in hand.
    public sealed record SelectionMarkInfo(int PageNumber, string State, int Offset, double Confidence, IReadOnlyList<PolygonPoint> Polygon);

    // Id only matters if a caller ever fetches the actual cropped figure image via the
    // figures output endpoint - Offset/Caption are enough for text-only consumers.
    // Elements are DI's own JSON-pointer refs (e.g. "/paragraphs/12") into the paragraphs
    // that discuss/describe this figure - broader than just its Caption.
    public sealed record FigureInfo(string? Caption, int? Offset, int PageNumber, string? Id, IReadOnlyList<string> Elements);

    public sealed record PolygonPoint(float X, float Y);

    public sealed record LineInfo(string Content, int? Offset, int PageNumber, IReadOnlyList<PolygonPoint> Polygon);

    // One DI section's extent, captured as every Span rather than a single anchor Offset
    // (the pattern Heading/TableInfo/FigureInfo use): a section only means something as a
    // start-to-end range, so slicing its content the way GetPages slices per-page content
    // needs every span, not just the first one.
    public sealed record SectionSpan(int Offset, int Length);

    // A DI-detected section - the closest thing prebuilt-layout offers to real semantic
    // chunk boundaries, as opposed to the page-only boundaries GetPages relies on today.
    // Elements are DI's own JSON-pointer refs (e.g. "/paragraphs/15", "/tables/2",
    // "/sections/3" for a nested subsection) into whichever paragraphs/tables/figures/
    // subsections this section contains. Resolving those refs into actual content/building
    // a section tree is left to a future chunk-builder, not done at extraction time.
    public sealed record SectionInfo(IReadOnlyList<SectionSpan> Spans, IReadOnlyList<string> Elements);

    // Average OCR word-confidence for one page - a data-quality signal only, never a
    // chunk-boundary signal (that's what SectionInfo is for). Kept as its own record
    // rather than folded into PageDimensions: PageDimensions is DI's physical-geometry
    // measurement of a page, this is DI's confidence in what it read off that page - two
    // different concerns that happen to both be "one value per page".
    public sealed record PageQuality(int PageNumber, double AverageWordConfidence);

    // One non-fatal warning DI attached to the whole-document analysis (e.g. a page that
    // partially failed OCR) - distinct from the zero-pages case DIAnalyzeDocumentAsync
    // already treats as an outright failure. Wraps Azure's DocumentIntelligenceWarning so
    // callers of this project's models don't need a reference to the Azure SDK type.
    public sealed record AnalysisWarning(string? Code, string? Message, string? Target);

    // Raw structural data extracted from one PDF - not the final chunk metadata.
    // - At extraction time, chunk boundaries don't exist yet, so this record does NOT
    //   assemble chunks itself.
    // - It simply bundles everything the extraction step already produces for free.
    // - A later step builds the real ChunkMetadata by matching these items up using
    //   their Offset values.
    // - NativeMetadata/Bookmarks live once, at the top level of PDFExtractionResult -
    //   not duplicated in here.
    public sealed record PdfDocumentStructure(
        IReadOnlyList<Heading> Headings,               // title / sectionHeading roles only
        IReadOnlyList<Heading> Boilerplate,             // pageHeader / pageFooter / footnote / pageNumber roles
        IReadOnlyList<TableInfo> Tables,
        IReadOnlyList<PageDimensions> PageDimensions,
        IReadOnlyList<SelectionMarkInfo> SelectionMarks,
        IReadOnlyList<FigureInfo> Figures,
        IReadOnlyList<LineInfo> Lines,
        IReadOnlyList<SectionInfo> Sections,
        IReadOnlyList<PageQuality> PageQuality);

    // Result of calling the (paid) Document Intelligence analyze API once:
    // - Ok = true  -> Result contains a successful, non-empty analysis (at least one page -
    //   a zero-page result is deliberately folded into Ok = false, see DIAnalyzeDocumentAsync).
    // - Ok = false -> Error contains a typed reason instead of throwing an exception.
    //   This lets callers check Error.Reason and decide what to do
    //   (e.g. "Throttled" is worth retrying, "DiServiceError" probably isn't).
    public sealed record AnalyzeOutcome(bool Ok, AnalyzeResult? Result, ExtractionError? Error)
    {
        // Non-fatal findings from the analyze call itself (e.g. the non-BMP character
        // check) - only ever populated when Ok is true, since Error already covers the
        // failure case. Empty (not null) otherwise.
        public IReadOnlyList<AnalysisWarning> Warnings { get; init; } = [];
    }

    // Result of PDFDocumentAnalyzer.AnalyzeDocumentAsync (the DI-scoped step only -
    // preflight/native-metadata are separate steps, combined by DocumentIntelligenceExtractor
    // into the final PDFExtractionResult):
    // - Ok = true  -> RawContent/Pages/Structure/EstimatedCostUsd are populated.
    // - Ok = false -> Error explains what went wrong, whether the failure happened during
    //   preflight checks or during the paid Document Intelligence call itself.
    public sealed record PDFStructureExtractorResult(
        bool Ok,
        string? RawContent,                            // analysis.Content, unsplit, before per-page assembly
        IReadOnlyList<PdfPageRecord>? Pages,
        PdfDocumentStructure? Structure,
        decimal? EstimatedCostUsd,
        ExtractionError? Error)
    {
        // Empty (not null) when Ok is false - there's no analysis to have warned about.
        public IReadOnlyList<AnalysisWarning> Warnings { get; init; } = [];
    }
}
