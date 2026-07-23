namespace AgenticRagApp.Common.Models;

// Declared once, was identical in both pipelines.
public sealed record CleaningError(string? DocumentId, string Message)
    : PipelineIssueBase(DocumentId, Message);
