using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public interface IIndexDocumentService
{
    Task<Dictionary<string, DateTimeOffset>> GetIndexedDocumentDatesAsync(CancellationToken ct = default);
    Task<(int Succeeded, int Failed)> UpsertDocumentsAsync(IEnumerable<ProtocolDocument> documents, CancellationToken ct = default);
    Task<int> DeleteDocumentsAsync(IEnumerable<string> documentIds, CancellationToken ct = default);

    // Whole-index aggregates (document count, storage size) — see IndexDocumentService for detail.
    Task<(long DocumentCount, long StorageSizeBytes)> GetStatisticsAsync(CancellationToken ct = default);

    // Compares the given stats against the last saved baseline and returns any drift red flags,
    // then saves the given stats as the new baseline for the next run.
    Task<IReadOnlyList<string>> CheckDriftAsync(long documentCount, long storageSizeBytes, CancellationToken ct = default);
}
