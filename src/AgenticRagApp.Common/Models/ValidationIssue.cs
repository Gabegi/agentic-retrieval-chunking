namespace AgenticRagApp.Common.Models;

public sealed record ValidationIssue(
    string Stage,    // "Parse:Pages", "Parse:Index", "Join", "Clean"
    string Severity, // "Error" or "Warning"
    string DocumentId,
    string Message,
    // Structured failure category, carried over from ExtractionError.Reason when
    // present (currently only PDF file-level open failures set it).
    PdfOpenFailureReason? Reason = null)
    : PipelineIssueBase(DocumentId, Message);
