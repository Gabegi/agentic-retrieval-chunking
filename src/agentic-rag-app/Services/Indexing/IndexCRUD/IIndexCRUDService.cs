namespace ProtocolsIndexer.Services;

public interface IIndexCRUDService
{
    // Returns the last_modified_date for every document currently in the index, keyed by document_id.
    // Used by the pipeline to detect unchanged documents (skip) and removed documents (delete).
    Task<Dictionary<string, DateTimeOffset>> GetIndexedDocumentDatesAsync(CancellationToken ct = default);

    // Deletes all chunks in the index that belong to the given document IDs.
    // Used for both removed documents and changed documents (delete-then-reinsert,
    // because chunk count can grow/shrink on re-chunking and mergeOrUpload would leave orphans).
    Task DeleteDocumentsAsync(IEnumerable<string> documentIds, CancellationToken ct = default);
}
