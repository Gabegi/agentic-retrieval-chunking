using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

public interface IIndexDocumentService
{
    Task<Dictionary<string, DateTimeOffset>> GetCurrentIndexedDocumentDatesAsync(CancellationToken ct = default);
    Task<(int Succeeded, int Failed)> UpsertDocumentsAsync(IEnumerable<DocumentChunk> documents, CancellationToken ct = default);

    // The two halves of what used to be one "delete everything for these documents" call.
    // Split so a caller can diff the result against a "keep" set (e.g. chunks just
    // re-uploaded) before deciding what's actually stale - see UploadService.
    Task<IReadOnlyList<string>> GetChunkIdsForDocumentsAsync(IEnumerable<string> documentIds, CancellationToken ct = default);
    Task<int> DeleteChunksByIdAsync(IEnumerable<string> chunkIds, CancellationToken ct = default);

    // Whole-index aggregates (document count, storage size) — see IndexDocumentService for detail.
    Task<(long DocumentCount, long StorageSizeBytes)> GetStatisticsAsync(CancellationToken ct = default);

    // Compares the given stats against the last saved baseline and returns any drift red flags,
    // then saves the given stats as the new baseline for the next run.
    Task<IReadOnlyList<string>> CheckDriftAsync(long documentCount, long storageSizeBytes, CancellationToken ct = default);
}
