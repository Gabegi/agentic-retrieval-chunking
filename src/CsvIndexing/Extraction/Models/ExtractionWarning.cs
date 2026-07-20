namespace CsvIndexing.Models;

public class ExtractionWarning
{
    public int?    RowNumber  { get; init; }
    public string? DocumentId { get; init; }
    public string  Message    { get; init; } = "";
}
