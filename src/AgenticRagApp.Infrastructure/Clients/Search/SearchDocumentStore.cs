using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging;

namespace AgenticRagApp.Infrastructure.Clients.Search;

public class SearchDocumentStore : ISearchDocumentStore
{
    private readonly SearchClient                    _client;
    private readonly ILogger<SearchDocumentStore>     _logger;

    public SearchDocumentStore(SearchClient client, ILogger<SearchDocumentStore> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<(int Succeeded, int Failed, int Batches)> UpsertDocumentsAsync<T>(IEnumerable<T> documents, CancellationToken ct = default)
    {
        var succeeded = 0;
        var failed    = 0;
        var batches   = 0;

        foreach (var batch in documents.ToList().Chunk(1000))
        {
            batches++;
            var response = await _client.UploadDocumentsAsync(batch, cancellationToken: ct);
            foreach (var result in response.Value.Results)
            {
                if (result.Succeeded)
                {
                    succeeded++;
                }
                else
                {
                    _logger.LogWarning("Failed to upsert {Key}: {Error}", result.Key, result.ErrorMessage);
                    failed++;
                }
            }
        }

        return (succeeded, failed, batches);
    }

    public async Task<Dictionary<string, DateTimeOffset>> GetCurrentIndexedDocumentDatesAsync(CancellationToken ct = default)
    {
        var result  = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var options = new SearchOptions
        {
            Select = { "document_id", "last_modified_date" },
            Size   = 1000,
        };

        var response = await _client.SearchAsync<SearchDocument>("*", options, ct);
        await foreach (var r in response.Value.GetResultsAsync().WithCancellation(ct))
        {
            if (r.Document.TryGetValue("document_id",      out var idObj)   && idObj   is string docId &&
                r.Document.TryGetValue("last_modified_date", out var dateObj) && dateObj is DateTimeOffset date)
                result.TryAdd(docId, date);
        }

        return result;
    }

    public async Task<IReadOnlyList<string>> GetChunkIdsForDocumentsAsync(IEnumerable<string> documentIds, CancellationToken ct = default)
    {
        var idList = documentIds.ToList();
        if (idList.Count == 0) return [];

        var chunkIds = new List<string>();

        foreach (var batch in idList.Chunk(50))
        {
            var escaped = batch.Select(id => id.Replace("'", "''"));
            var filter  = $"search.in(document_id, '{string.Join(",", escaped)}', ',')";
            var options = new SearchOptions { Filter = filter, Select = { "id" }, Size = 1000 };

            var response = await _client.SearchAsync<SearchDocument>("*", options, ct);
            await foreach (var r in response.Value.GetResultsAsync().WithCancellation(ct))
            {
                if (r.Document.TryGetValue("id", out var idObj) && idObj is string chunkId)
                    chunkIds.Add(chunkId);
            }
        }

        return chunkIds;
    }

    public async Task<int> DeleteChunksByIdAsync(IEnumerable<string> chunkIds, CancellationToken ct = default)
    {
        var idList = chunkIds.ToList();
        if (idList.Count == 0) return 0;

        foreach (var batch in idList.Chunk(1000))
        {
            var actions = batch.Select(id => IndexDocumentsAction.Delete("id", id));
            await _client.IndexDocumentsAsync(IndexDocumentsBatch.Create(actions.ToArray()), cancellationToken: ct);
        }

        return idList.Count;
    }
}
