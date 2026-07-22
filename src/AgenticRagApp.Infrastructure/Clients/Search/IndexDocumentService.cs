using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Configuration;

namespace AgenticRagApp.Infrastructure.Clients.Search;

public class IndexDocumentService : IIndexDocumentService
{
    private readonly ISearchDocumentStore          _documentStore;
    private readonly ISearchIndexStore             _indexStore;
    private readonly IndexerConfig                 _config;
    private readonly ILogger<IndexDocumentService> _logger;

    public IndexDocumentService(
        IndexerConfig config, ISearchDocumentStore documentStore, ISearchIndexStore indexStore, ILogger<IndexDocumentService> logger)
    {
        _documentStore = documentStore;
        _indexStore    = indexStore;
        _config        = config;
        _logger        = logger;
    }

    public async Task<(int Succeeded, int Failed)> UpsertDocumentsAsync<T>(IEnumerable<T> documents, CancellationToken ct = default)
    {
        var (succeeded, failed, batches) = await _documentStore.UpsertDocumentsAsync(documents, ct);
        _logger.LogInformation("Upsert complete — {Succeeded} succeeded, {Failed} failed ({Batches} batch(es))", succeeded, failed, batches);
        return (succeeded, failed);
    }

    public async Task<Dictionary<string, DateTimeOffset>> GetCurrentIndexedDocumentDatesAsync(CancellationToken ct = default)
    {
        var result = await _documentStore.GetCurrentIndexedDocumentDatesAsync(ct);
        _logger.LogInformation("Found {Count} documents currently in index", result.Count);
        return result;
    }

    public Task<IReadOnlyList<string>> GetChunkIdsForDocumentsAsync(IEnumerable<string> documentIds, CancellationToken ct = default) =>
        _documentStore.GetChunkIdsForDocumentsAsync(documentIds, ct);

    public async Task<int> DeleteChunksByIdAsync(IEnumerable<string> chunkIds, CancellationToken ct = default)
    {
        var count = await _documentStore.DeleteChunksByIdAsync(chunkIds, ct);
        _logger.LogInformation("Deleted {ChunkCount} chunks", count);
        return count;
    }

    public Task<(long DocumentCount, long StorageSizeBytes)> GetStatisticsAsync(CancellationToken ct = default) =>
        _indexStore.GetStatisticsAsync(_config.SearchIndexName, ct);
}
