namespace ProtocolsIndexer.Models;

// Mirrors CSV's JoinResult. No inactive-document bucket — PDFs have no active/
// inactive flag, unlike Zenya's index. Reuses the existing (source-agnostic)
// JoinError type rather than a Pdf-prefixed duplicate.
public class PdfJoinResult
{
    private readonly List<PdfJoinedPageRecord> _joined              = [];
    private readonly List<JoinError>           _errors              = [];
    private readonly List<JoinError>           _dataQualityWarnings = [];
    private readonly List<PdfIndexRecord>      _skippedIndexRecords = [];

    public IReadOnlyList<PdfJoinedPageRecord> Joined              => _joined;
    public IReadOnlyList<JoinError>           Errors              => _errors;              // page → no matching index record
    public IReadOnlyList<JoinError>           DataQualityWarnings => _dataQualityWarnings; // e.g. duplicate index BlobName
    public IReadOnlyList<PdfIndexRecord>      SkippedIndexRecords => _skippedIndexRecords; // index record with no pages — expected

    internal void AddJoined(PdfJoinedPageRecord r)          => _joined.Add(r);
    internal void AddToNotFound(JoinError e)                => _errors.Add(e);
    internal void AddToDuplicates(JoinError w)               => _dataQualityWarnings.Add(w);
    internal void AddToIndexWithoutPages(PdfIndexRecord r)  => _skippedIndexRecords.Add(r);
}
