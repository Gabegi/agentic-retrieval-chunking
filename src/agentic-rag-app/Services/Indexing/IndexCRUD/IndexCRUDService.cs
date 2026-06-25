using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Azure.Core;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Configuration;

namespace ProtocolsIndexer.Services;

// Handles document-level CRUD operations against the Azure AI Search index.
// IndexService owns schema lifecycle; this class owns the document data inside it.
public class IndexCRUDService : IIndexCRUDService
{
    private readonly SearchClient                _searchClient;
    private readonly ILogger<IndexCRUDService>  _logger;

    public IndexCRUDService(IndexerConfig config, TokenCredential credential, ILogger<IndexCRUDService> logger)
    {
        _searchClient = new SearchClient(new Uri(config.SearchEndpoint), config.SearchIndexName, credential);
        _logger       = logger;
    }

    // Pages through the entire index selecting only document_id + last_modified_date.
    // Deduplication is implicit: all chunks for the same document share the same values,
    // so TryAdd keeps the first occurrence and ignores the rest.
    public async Task<Dictionary<string, DateTimeOffset>> GetIndexedDocumentDatesAsync(CancellationToken ct = default)
    {
        var result  = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var options = new SearchOptions
        {
            Select = { "document_id", "last_modified_date" },
            Size   = 1000,
        };

        var response = await _searchClient.SearchAsync<SearchDocument>("*", options, ct);
        await foreach (var r in response.Value.GetResultsAsync().WithCancellation(ct))
        {
            if (r.Document.TryGetValue("document_id",      out var idObj)   && idObj   is string docId &&
                r.Document.TryGetValue("last_modified_date", out var dateObj) && dateObj is DateTimeOffset date)
                result.TryAdd(docId, date);
        }

        _logger.LogInformation("Found {Count} documents currently in index", result.Count);
        return result;
    }

    // Queries all chunk ids for the given document IDs, then submits batch delete actions.
    // Batches document IDs into groups of 50 to keep OData filter length manageable,
    // and chunk deletes into batches of 1000 per the push API limit.
    public async Task DeleteDocumentsAsync(IEnumerable<string> documentIds, CancellationToken ct = default)
    {
        var idList = documentIds.ToList();
        if (idList.Count == 0) return;

        var chunkIds = new List<string>();

        foreach (var batch in idList.Chunk(50))
        {
            var escaped = batch.Select(id => id.Replace("'", "''"));
            var filter  = $"search.in(document_id, '{string.Join(",", escaped)}', ',')";
            var options = new SearchOptions { Filter = filter, Select = { "id" }, Size = 1000 };

            var response = await _searchClient.SearchAsync<SearchDocument>("*", options, ct);
            await foreach (var r in response.Value.GetResultsAsync().WithCancellation(ct))
            {
                if (r.Document.TryGetValue("id", out var idObj) && idObj is string chunkId)
                    chunkIds.Add(chunkId);
            }
        }

        foreach (var batch in chunkIds.Chunk(1000))
        {
            var actions = batch.Select(id => IndexDocumentsAction.Delete("id", id));
            await _searchClient.IndexDocumentsAsync(IndexDocumentsBatch.Create(actions.ToArray()), cancellationToken: ct);
        }

        _logger.LogInformation("Deleted all chunks for {Count} documents", idList.Count);
    }
}
