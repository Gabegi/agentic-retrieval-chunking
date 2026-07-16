using Azure.AI.DocumentIntelligence;

namespace ProtocolsIndexer.Models
{
    // Return types used by PDFStructureExtractor's Get* methods:
    // - Each record below matches one Get* method one-to-one.
    // - This keeps callers focused only on the fields they actually asked for.

    // A single heading/title paragraph detected in the PDF:
    // - Offset = position of this heading inside the document's single combined text string.
    //   This is what later code uses to line headings up with other content (the "join key").
    // - PageNumber = which page the heading is on, for display/debugging only.
    //   It can't be used for ordering, because two headings on the same page look identical by page number.
    public sealed record Heading(string Content, string Role, int Offset, int PageNumber);

    public sealed record PageDimensions(int PageNumber, double? Width, double? Height, string Unit);

    public sealed record TableCellInfo(int RowIndex, int ColumnIndex, string Kind, string Content);

    public sealed record TableInfo(int RowCount, int ColumnCount, IReadOnlyList<TableCellInfo> Cells, int Offset, int PageNumber);

    public sealed record SelectionMarkInfo(int PageNumber, string State, int Offset);

    // Raw structural data extracted from one PDF - not the final chunk metadata.
    // - At extraction time, chunk boundaries don't exist yet, so this record does NOT
    //   assemble chunks itself.
    // - It simply bundles everything the extraction step already produces for free
    //   (headings, tables, page sizes, selection marks).
    // - A later step builds the real ChunkMetadata by matching these items up using
    //   their Offset values.
    public sealed record PdfStructureMetadata(
        DocMetadata NativeMetadata,
        IReadOnlyList<Heading> Headings,
        IReadOnlyList<Bookmark>? Bookmarks,
        IReadOnlyList<TableInfo> Tables,
        IReadOnlyList<PageDimensions> PageDimensions,
        IReadOnlyList<SelectionMarkInfo> SelectionMarks);

    // Result of calling the (paid) Document Intelligence analyze API once:
    // - Ok = true  -> Result contains the successful analysis.
    // - Ok = false -> Error contains a typed reason instead of throwing an exception.
    //   This lets callers check Error.Reason and decide what to do
    //   (e.g. "Throttled" is worth retrying, "DiServiceError" probably isn't).
    public sealed record AnalyzeOutcome(bool Ok, AnalyzeResult? Result, ExtractionError? Error);

    // Full result of ExtractPdfStructureAsync (the single entry point DocumentIntelligenceExtractor calls):
    // - Ok = true  -> Pages/Index/Metadata/EstimatedCostUsd are populated.
    // - Ok = false -> Error explains what went wrong, whether the failure happened during
    //   preflight checks or during the paid Document Intelligence call itself.
    // - Uses the same Ok/Error pattern as AnalyzeOutcome above.
    public sealed record PdfStructureExtraction(
        bool Ok,
        IReadOnlyList<PdfPageRecord>? Pages,
        PdfIndexRecord? Index,
        PdfStructureMetadata? Metadata,
        decimal? EstimatedCostUsd,
        ExtractionError? Error);
}
