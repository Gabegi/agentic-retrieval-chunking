using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Models;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Services;

// Handles document-level CRUD operations against the Azure AI Search index, plus the
// data-volume monitoring (stats + drift) that rides along with it.
// IndexService owns schema lifecycle; this class owns the document data inside it.
public class IndexDocumentService : IIndexDocumentService
{
    // Run-over-run doc-count swing beyond this is flagged as drift. Tune based on observed
    // corpus volatility — the source data doesn't churn more than this between runs today.
    private const double DriftThresholdPct = 0.15;

    private readonly ISearchDocumentStore          _documentStore;
    private readonly ISearchIndexStore             _indexStore;
    private readonly IndexerConfig                 _config;
    private readonly IRunReportWriter              _reportWriter;
    private readonly ILogger<IndexDocumentService>  _logger;

    public IndexDocumentService(
        IndexerConfig config, ISearchDocumentStore documentStore, ISearchIndexStore indexStore, IRunReportWriter reportWriter, ILogger<IndexDocumentService> logger)
    {
        _documentStore = documentStore;
        _indexStore    = indexStore;
        _config        = config;
        _reportWriter  = reportWriter;
        _logger        = logger;
    }

    // Maps each DocumentChunk down to SearchUploadChunk - the exact field set the Search
    // schema knows about - right here, at the last possible moment before handing off to
    // the generic upload path. DocumentChunk itself carries everything extraction produced
    // (needed for the blob hand-off between pipeline activities and the Stage 2 archive);
    // sending it to Search directly would include fields Search has no schema for and rejects.
    public async Task<(int Succeeded, int Failed)> UpsertDocumentsAsync(IEnumerable<DocumentChunk> documents, CancellationToken ct = default)
    {
        var uploadBatch = documents.Select(SearchUploadChunk.From).ToList();
        var (succeeded, failed, batches) = await _documentStore.UpsertDocumentsAsync(uploadBatch, ct);

        Instrumentation.UploadFailures.Add(failed);
        Instrumentation.DocsUpserted.Add(succeeded);
        Instrumentation.UploadBatchCount.Add(batches);
        _logger.LogInformation("Upsert complete — {Succeeded} succeeded, {Failed} failed", succeeded, failed);
        return (succeeded, failed);
    }

    // Pages through the entire index selecting only document_id + last_modified_date.
    public async Task<Dictionary<string, DateTimeOffset>> GetCurrentIndexedDocumentDatesAsync(CancellationToken ct = default)
    {
        var result = await _documentStore.GetCurrentIndexedDocumentDatesAsync(ct);
        _logger.LogInformation("Found {Count} documents currently in index", result.Count);
        return result;
    }

    // Queries all chunk ids currently in the index for the given document IDs.
    public Task<IReadOnlyList<string>> GetChunkIdsForDocumentsAsync(IEnumerable<string> documentIds, CancellationToken ct = default) =>
        _documentStore.GetChunkIdsForDocumentsAsync(documentIds, ct);

    // Submits batch delete actions for exactly the given chunk ids.
    public async Task<int> DeleteChunksByIdAsync(IEnumerable<string> chunkIds, CancellationToken ct = default)
    {
        var count = await _documentStore.DeleteChunksByIdAsync(chunkIds, ct);
        _logger.LogInformation("Deleted {ChunkCount} chunks", count);
        return count;
    }

    // Fetches whole-index aggregates from Azure AI Search:
    //   DocumentCount – total documents currently in the index
    //   StorageSize   – total storage the index is consuming, in bytes
    // No per-field or per-document detail is available from this API. Recorded as histograms
    // in every environment (not just dev) so drift dashboards/alerts have data to work with.
    public async Task<(long DocumentCount, long StorageSizeBytes)> GetStatisticsAsync(CancellationToken ct = default)
    {
        var (docCount, storageBytes) = await _indexStore.GetStatisticsAsync(_config.SearchIndexName, ct);

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
