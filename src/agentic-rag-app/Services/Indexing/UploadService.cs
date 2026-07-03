using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Models;
using ProtocolsIndexer.Observability;

namespace ProtocolsIndexer.Services;

// Owns the upload half of the indexing pipeline: upserts embedded ProtocolDocuments
// into Azure AI Search and takes a post-upload index stats/drift snapshot.
// Kept separate from EmbeddingService so the two concerns can evolve independently.
public class UploadService : IUploadService
{
    private readonly IIndexDocumentService      _indexDocumentService;
    private readonly ILogger<UploadService>     _logger;

    public UploadService(
        IIndexDocumentService  indexDocumentService,
        ILogger<UploadService> logger)
    {
        _indexDocumentService = indexDocumentService;
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
            var (docCount, storageBytes) = await _indexDocumentService.GetStatisticsAsync(ct);
            (indexDocCount, indexStorageBytes) = (docCount, storageBytes);
            redFlags.AddRange(await _indexDocumentService.CheckDriftAsync(docCount, storageBytes, ct));
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
