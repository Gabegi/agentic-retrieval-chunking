namespace ProtocolsIndexer.Models;

public class ExtractionResult<T>
{
    private readonly List<T>               _records = [];
    private readonly List<ExtractionError> _errors  = [];

    public IReadOnlyList<T>               Records  => _records;
    public IReadOnlyList<ExtractionError> Errors   => _errors;
    public int                            TotalRows => _records.Count + _errors.Count;

    internal void AddRecord(T record)              => _records.Add(record);
    internal void AddError(ExtractionError error)  => _errors.Add(error);
}
