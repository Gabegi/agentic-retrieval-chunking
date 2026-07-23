namespace AgenticRagApp.Common.Models;

// Declared once, was identical in both pipelines.
public sealed record CleaningWarning(string? DocumentId, string Message)
    : PipelineIssueBase(DocumentId, Message);
