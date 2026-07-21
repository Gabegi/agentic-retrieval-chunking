namespace AgenticRagApp.Indexing.Csv.Models;

public class ExtractionResult<T>
{
    private readonly List<T>                 _records  = [];
    private readonly List<ExtractionError>   _errors   = [];
    private readonly List<ExtractionWarning> _warnings = [];

    public IReadOnlyList<T>                 Records       => _records;
    public IReadOnlyList<ExtractionError>   Errors        => _errors;
    public IReadOnlyList<ExtractionWarning> Warnings      => _warnings;
    public int                              RowsAttempted => _records.Count + _errors.Count;

    internal void AddRecord(T record)                   => _records.Add(record);
    internal void AddError(ExtractionError error)       => _errors.Add(error);
    internal void AddWarning(ExtractionWarning warning) => _warnings.Add(warning);
}
