namespace AgenticRagApp.Observability.Reports;

public record ValidationIssueEntry(string Stage, string Severity, string DocumentId, string Message);

public record SpotCheckEntry(string DocumentId, string Title, string ContentPreview);
