namespace AgenticRagApp.Common.Models;

public sealed record ExtractionError(
    int RowNumber,
    string? DocumentId,
    string Message,
    // Structured failure category. Only set by extractors that distinguish
    // exception types at the point of failure (currently PdfPigExtractor's
    // open/validate step) - null for row/page-level errors that only have
    // free text (CSV rows, per-page extraction failures). Concrete type is whichever
    // source produced it - PdfOpenFailureReason or CsvOpenFailureReason.
    FailureReasonBase? Reason = null)
    : PipelineIssueBase(DocumentId, Message);
