namespace AgenticRagApp.Models;

public class ExtractionError
{
    public int     RowNumber  { get; init; }
    public string? DocumentId { get; init; }
    public string  Message    { get; init; } = "";

    // Structured failure category. Only set by extractors that distinguish
    // exception types at the point of failure (currently PdfPigExtractor's
    // open/validate step) - null for row/page-level errors that only have
    // free text (CSV rows, per-page extraction failures).
    public PdfOpenFailureReason? Reason { get; init; }
}
