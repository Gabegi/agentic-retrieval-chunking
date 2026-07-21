namespace AgenticRagApp.Infrastructure.Clients.Search;

// Generic wrapper around SearchClient — document-level CRUD against whatever index it's
// scoped to. Doc-type-specific mapping (which fields a chunk maps to) happens before
// documents reach UpsertDocumentsAsync; this only ever moves already-shaped documents.
public interface ISearchDocumentStore
{
    // Batches internally (1000 per call — the Search push API limit). Returns aggregate
    // counts plus how many batches it took; callers that need per-document detail should
    // check the SDK response themselves via a lower-level call — this is the generic path
    // everything else uses.
    Task<(int Succeeded, int Failed, int Batches)> UpsertDocumentsAsync<T>(IEnumerable<T> documents, CancellationToken ct = default);

    // Pages through the entire index selecting document_id + last_modified_date only.
    Task<Dictionary<string, DateTimeOffset>> GetCurrentIndexedDocumentDatesAsync(CancellationToken ct = default);

    // Batches document IDs into groups of 50 to keep the OData filter length manageable.
    Task<IReadOnlyList<string>> GetChunkIdsForDocumentsAsync(IEnumerable<string> documentIds, CancellationToken ct = default);

    Task<int> DeleteChunksByIdAsync(IEnumerable<string> chunkIds, CancellationToken ct = default);
}
