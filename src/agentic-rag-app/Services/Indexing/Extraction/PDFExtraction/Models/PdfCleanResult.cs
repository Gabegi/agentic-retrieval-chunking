namespace AgenticRag.Models;

// Mirrors CSV's CleanResult. Reuses the existing (source-agnostic) CleaningError/
// CleaningWarning types rather than Pdf-prefixed duplicates.
public class PdfCleanResult
{
    private readonly List<CleanedPdfPageRecord> _records  = [];
    private readonly List<CleaningError>        _errors   = [];
    private readonly List<CleaningWarning>      _warnings = [];

    public IReadOnlyList<CleanedPdfPageRecord> Records  => _records;
    public IReadOnlyList<CleaningError>        Errors   => _errors;
    public IReadOnlyList<CleaningWarning>      Warnings => _warnings;

    public int MojibakeRepairedPages { get; private set; }

    internal void AddRecord(CleanedPdfPageRecord r) => _records.Add(r);
    internal void AddError(CleaningError e)         => _errors.Add(e);
    internal void AddWarning(CleaningWarning w)     => _warnings.Add(w);
    internal void CountMojibakeRepaired()           => MojibakeRepairedPages++;
}
