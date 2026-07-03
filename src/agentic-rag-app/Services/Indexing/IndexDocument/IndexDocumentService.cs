using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Models;
using Azure.Core;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Configuration;
using ProtocolsIndexer.Models;
using ProtocolsIndexer.Observability;
using ProtocolsIndexer.Observability.Reports;

namespace ProtocolsIndexer.Services;

// Handles document-level CRUD operations against the Azure AI Search index, plus the
// data-volume monitoring (stats + drift) that rides along with it.
// IndexService owns schema lifecycle; this class owns the document data inside it.
public class IndexDocumentService : IIndexDocumentService
{
    // Run-over-run doc-count swing beyond this is flagged as drift. Tune based on observed
    // corpus volatility — the source data doesn't churn more than this between runs today.
    private const double DriftThresholdPct = 0.15;

    private readonly SearchClient                  _searchClient;
    private readonly SearchIndexClient             _indexClient;
    private readonly IndexerConfig                 _config;
    private readonly IRunReportWriter              _reportWriter;
    private readonly ILogger<IndexDocumentService>  _logger;

    public IndexDocumentService(
        IndexerConfig config, TokenCredential credential, IRunReportWriter reportWriter, ILogger<IndexDocumentService> logger)
    {
        _searchClient = new SearchClient(new Uri(config.SearchEndpoint), config.SearchIndexName, credential);
        _indexClient  = new SearchIndexClient(new Uri(config.SearchEndpoint), credential);
        _config       = config;
        _reportWriter = reportWriter;
        _logger       = logger;
    }

    // Uploads embedded documents to the index in batches of 1000 (push API limit).
    public async Task<(int Succeeded, int Failed)> UpsertDocumentsAsync(IEnumerable<ProtocolDocument> documents, CancellationToken ct = default)
    {
        var docList   = documents.ToList();
        var succeeded = 0;
        var failed    = 0;
        var batches   = 0;

        foreach (var batch in docList.Chunk(1000))
        {
            batches++;
            var response = await _searchClient.UploadDocumentsAsync(batch, cancellationToken: ct);
            foreach (var result in response.Value.Results)
            {
                if (!result.Succeeded)
                {
                    _logger.LogWarning("Failed to upsert {Key}: {Error}", result.Key, result.ErrorMessage);
                    Instrumentation.UploadFailures.Add(1);
                    failed++;
                }
                else
                {
                    succeeded++;
                }
            }
        }

        Instrumentation.DocsUpserted.Add(succeeded);
        Instrumentation.UploadBatchCount.Add(batches);
        _logger.LogInformation("Upsert complete — {Succeeded} succeeded, {Failed} failed", succeeded, failed);
        return (succeeded, failed);
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

    // Queries all chunk ids currently in the index for the given document IDs. Batches
    // document IDs into groups of 50 to keep the OData filter length manageable.
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

            var response = await _searchClient.SearchAsync<SearchDocument>("*", options, ct);
            await foreach (var r in response.Value.GetResultsAsync().WithCancellation(ct))
            {
                if (r.Document.TryGetValue("id", out var idObj) && idObj is string chunkId)
                    chunkIds.Add(chunkId);
            }
        }

        return chunkIds;
    }

    // Submits batch delete actions for exactly the given chunk ids, in batches of 1000
    // per the push API limit.
    public async Task<int> DeleteChunksByIdAsync(IEnumerable<string> chunkIds, CancellationToken ct = default)
    {
        var idList = chunkIds.ToList();
        if (idList.Count == 0) return 0;

        foreach (var batch in idList.Chunk(1000))
        {
            var actions = batch.Select(id => IndexDocumentsAction.Delete("id", id));
            await _searchClient.IndexDocumentsAsync(IndexDocumentsBatch.Create(actions.ToArray()), cancellationToken: ct);
        }

        _logger.LogInformation("Deleted {ChunkCount} chunks", idList.Count);
        return idList.Count;
    }

    // Fetches whole-index aggregates from Azure AI Search (GET .../indexes/{name}/stats):
    //   DocumentCount – total documents currently in the index
    //   StorageSize   – total storage the index is consuming, in bytes
    // No per-field or per-document detail is available from this API. Recorded as histograms
    // in every environment (not just dev) so drift dashboards/alerts have data to work with.
    public async Task<(long DocumentCount, long StorageSizeBytes)> GetStatisticsAsync(CancellationToken ct = default)
    {
        var response = await _indexClient.GetIndexStatisticsAsync(_config.SearchIndexName, ct);
        var (docCount, storageBytes) = (response.Value.DocumentCount, response.Value.StorageSize);

        Instrumentation.IndexDocumentCount.Record(docCount);
        Instrumentation.IndexStorageSizeBytes.Record(storageBytes);

        return (docCount, storageBytes);
    }

    // Compares against the last saved baseline and flags a run-over-run doc-count swing
    // beyond DriftThresholdPct, then saves the given stats as the new baseline regardless.
    public async Task<IReadOnlyList<string>> CheckDriftAsync(long documentCount, long storageSizeBytes, CancellationToken ct = default)
    {
        var redFlags = new List<string>();
        var previous = await _reportWriter.GetLastIndexStatsAsync(ct);
        if (previous is { DocumentCount: > 0 } baseline)
        {
            var deltaPct = (documentCount - baseline.DocumentCount) / (double)baseline.DocumentCount;
            if (Math.Abs(deltaPct) > DriftThresholdPct)
            {
                redFlags.Add($"index_doc_count_drift:{deltaPct:+0.0%;-0.0%} ({baseline.DocumentCount} -> {documentCount})");
                _logger.LogWarning("Index doc count drift detected: {Previous} -> {Current} ({DeltaPct:P1})",
                    baseline.DocumentCount, documentCount, deltaPct);
            }
        }

        await _reportWriter.SaveLastIndexStatsAsync(documentCount, storageSizeBytes, ct);
        return redFlags;
    }
}
