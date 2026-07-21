namespace AgenticRagApp.Indexing.Csv.Models;

public class ExtractionError
{
    public int     RowNumber  { get; init; }
    public string? DocumentId { get; init; }
    public string  Message    { get; init; } = "";
}
