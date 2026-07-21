using Azure;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.KnowledgeBases;
using Azure.Search.Documents.KnowledgeBases.Models;

namespace AgenticRagApp.Infrastructure.Clients.Search;

public class SearchIndexStore : ISearchIndexStore
{
    private readonly SearchIndexClient _client;

    public SearchIndexStore(SearchIndexClient client) => _client = client;

    public async Task<bool> EnsureIndexAsync(SearchIndex definition, CancellationToken ct = default)
    {
        try
        {
            await _client.GetIndexAsync(definition.Name, ct);
            return false;
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }

        await _client.CreateOrUpdateIndexAsync(definition, cancellationToken: ct);
        return true;
    }

    public async Task<(long DocumentCount, long StorageSizeBytes)> GetStatisticsAsync(string indexName, CancellationToken ct = default)
    {
        var response = await _client.GetIndexStatisticsAsync(indexName, ct);
        return (response.Value.DocumentCount, response.Value.StorageSize);
    }

    public async Task CreateOrUpdateKnowledgeSourceAsync(SearchIndexKnowledgeSource source, CancellationToken ct = default) =>
        await _client.CreateOrUpdateKnowledgeSourceAsync(source, onlyIfUnchanged: false, ct);

    public async Task CreateOrUpdateKnowledgeBaseAsync(KnowledgeBase knowledgeBase, CancellationToken ct = default) =>
        await _client.CreateOrUpdateKnowledgeBaseAsync(knowledgeBase, onlyIfUnchanged: false, ct);
}
