namespace CsvIndexing.Models;

public class ValidationIssue
{
    public string Stage      { get; init; } = "";  // "Parse:Pages", "Parse:Index", "Join", "Clean"
    public string Severity   { get; init; } = "";  // "Error" or "Warning"
    public string DocumentId { get; init; } = "";
    public string Message    { get; init; } = "";
}
