using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Models;
using ProtocolsIndexer.Observability;
using ProtocolsIndexer.Observability.Reports;

namespace ProtocolsIndexer.Services;

// Owns the upload half of the indexing pipeline: upserts embedded ProtocolDocuments
// into Azure AI Search, takes a post-upload index stats snapshot, and flags corpus drift.
// Kept separate from EmbeddingService so the two concerns can evolve independently.
public class UploadService : IUploadService
{
    // Run-over-run doc-count swing beyond this is flagged as drift. Tune based on observed
    // corpus volatility — the source data doesn't churn more than this between runs today.
    private const double DriftThresholdPct = 0.15;

    private readonly IIndexDocumentService      _indexDocumentService;
    private readonly IIndexService              _indexService;
    private readonly IRunReportWriter           _reportWriter;
    private readonly ILogger<UploadService>     _logger;

    public UploadService(
        IIndexDocumentService  indexDocumentService,
        IIndexService          indexService,
        IRunReportWriter       reportWriter,
        ILogger<UploadService> logger)
    {
        _indexDocumentService = indexDocumentService;
        _indexService         = indexService;
        _reportWriter         = reportWriter;
        _logger               = logger;
    }

    public async Task<UploadResult> UploadDocumentsAsync(
        IEnumerable<ProtocolDocument> documents, CancellationToken ct = default)
    {
        var (succeeded, failed) = await _indexDocumentService.UpsertDocumentsAsync(documents, ct);

        _logger.LogInformation("Upload complete — {Succeeded} succeeded, {Failed} failed", succeeded, failed);

        // Stats snapshot taken after upload. Azure Search stats lag live writes by minutes —
        // use for corpus drift checks only, not for "did this run add N chunks" (use succeeded/failed).
        long? indexDocCount = null, indexStorageBytes = null;
        var redFlags = new List<string>();
        try
        {
            var (docCount, storageBytes) = await _indexService.GetStatisticsAsync(ct);
            (indexDocCount, indexStorageBytes) = (docCount, storageBytes);

            // Exported in every environment (unlike IndexRunReport, which is dev-only) so drift
            // dashboards/alerts have data to work with in prod, not just local runs.
            Instrumentation.IndexDocumentCount.Record(docCount);
            Instrumentation.IndexStorageSizeBytes.Record(storageBytes);

            var previous = await _reportWriter.GetLastIndexStatsAsync(ct);
            if (previous is { DocumentCount: > 0 } baseline)
            {
                var deltaPct = (docCount - baseline.DocumentCount) / (double)baseline.DocumentCount;
                if (Math.Abs(deltaPct) > DriftThresholdPct)
                {
                    redFlags.Add($"index_doc_count_drift:{deltaPct:+0.0%;-0.0%} ({baseline.DocumentCount} -> {docCount})");
                    _logger.LogWarning("Index doc count drift detected: {Previous} -> {Current} ({DeltaPct:P1})",
                        baseline.DocumentCount, docCount, deltaPct);
                }
            }

            await _reportWriter.SaveLastIndexStatsAsync(docCount, storageBytes, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Index stats snapshot failed — upload results are unaffected");
            Instrumentation.PipelineFailures.Add(1, new KeyValuePair<string, object?>("stage", "stats_snapshot"));
        }

        return new UploadResult(
            DocsUploaded:                  succeeded,
            DocsFailed:                    failed,
            IndexDocumentCountSnapshot:    indexDocCount,
            IndexStorageSizeBytesSnapshot: indexStorageBytes,
            RedFlags:                      redFlags);
    }
}
