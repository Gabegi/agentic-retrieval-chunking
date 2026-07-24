namespace AgenticRagApp.Common.Models;

// Declared once, was identical in both pipelines.
public sealed record ExtractionWarning(int? RowNumber, string? DocumentId, string Message)
    : PipelineIssueBase(DocumentId, Message);
