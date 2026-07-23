namespace AgenticRagApp.Common.Models;

// Base for every per-document problem report across both pipelines.
public abstract record PipelineIssueBase(string? DocumentId, string Message);
