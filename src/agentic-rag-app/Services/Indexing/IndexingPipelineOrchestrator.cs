using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Models;
using ProtocolsIndexer.Observability;
using ProtocolsIndexer.Observability.Reports;

namespace ProtocolsIndexer.Services;

// Source-agnostic indexing pipeline: resolves the right extractor by source key,
// then chunks, embeds, and uploads. Adding a new source means registering
// a new IExtractionOrchestrator — no changes here.
public class IndexingPipelineOrchestrator : IIndexingPipelineOrchestrator
{
    private readonly Dictionary<string, IExtractionOrchestrator> _extractors;
    private readonly IChunkingService                            _chunkingService;
    private readonly IEmbeddingService                           _embeddingService;
    private readonly IIndexService                               _indexService;
    private readonly IIndexDocumentService                       _indexDocumentService;
    private readonly ILogger<IndexingPipelineOrchestrator>       _logger;

    public IndexingPipelineOrchestrator(
        IEnumerable<IExtractionOrchestrator>  extractors,
        IChunkingService                      chunkingService,
        IEmbeddingService                     embeddingService,
        IIndexService                         indexService,
        IIndexDocumentService                 indexDocumentService,
        ILogger<IndexingPipelineOrchestrator> logger)
    {
        _extractors           = extractors.ToDictionary(e => e.Source, StringComparer.OrdinalIgnoreCase);
        _chunkingService      = chunkingService;
        _embeddingService     = embeddingService;
        _indexService         = indexService;
        _indexDocumentService = indexDocumentService;
        _logger               = logger;
    }

    public async Task<(IReadOnlyList<ExtractionDocument> Docs, ExtractionStats Stats)> ExtractAsync(
        string source, bool forceReindex = false, CancellationToken ct = default)
    {
        if (!_extractors.TryGetValue(source, out var extractor))
            throw new ArgumentException(
                $"No extractor registered for source '{source}'. Available: {string.Join(", ", _extractors.Keys)}");

        await _indexService.EnsureIndexAsync();

        _logger.LogInformation("Extracting from source '{Source}'", source);
        var output = await extractor.ExtractDocumentsAsync(ct);

        var indexedDates = forceReindex
            ? new Dictionary<string, DateTimeOffset>()
            : await _indexDocumentService.GetIndexedDocumentDatesAsync(ct);

        var toProcess = new List<ExtractionDocument>();
        var toDelete  = new List<string>();
        var newCount  = 0;
        var updated   = 0;

        foreach (var doc in output.Docs)
        {
            if (!indexedDates.TryGetValue(doc.SourceId, out var indexedDate))
            {
                toProcess.Add(doc);
                newCount++;
                continue;
            }

            var modifiedStr = doc.Metadata.GetValueOrDefault("last_modified_date");
            if (DateTimeOffset.TryParse(modifiedStr, out var modifiedDate) && modifiedDate <= indexedDate)
                continue;

            toDelete.Add(doc.SourceId);
            toProcess.Add(doc);
            updated++;
        }

        if (toDelete.Count > 0)
        {
            _logger.LogInformation("Deleting stale chunks for {Count} changed documents", toDelete.Count);
            await _indexDocumentService.DeleteDocumentsAsync(toDelete, ct);
        }

        var skipped   = output.Docs.Count - toProcess.Count;
        var sourceTag = new KeyValuePair<string, object?>("source", source);

        _logger.LogInformation("Skipping {Skipped} unchanged, processing {Count} new/changed documents",
            skipped, toProcess.Count);

        Instrumentation.DocsExtracted.Add(output.Docs.Count, sourceTag);
        Instrumentation.DocsSkipped.Add(skipped,             sourceTag);
        Instrumentation.DocsNew.Add(newCount,                 sourceTag);
        Instrumentation.DocsUpdated.Add(updated,              sourceTag);
        Instrumentation.DocsDeleted.Add(toDelete.Count,       sourceTag);

        var stats = new ExtractionStats(
            DocsToProcess:          toProcess.Count,
            DocsSkipped:            skipped,
            DocsNew:                newCount,
            DocsUpdated:            updated,
            DocsDeleted:            toDelete.Count,
            ValidationErrors:       output.ValidationErrors,
            ValidationWarnings:     output.ValidationWarnings,
            ReconciliationProblems: output.ReconciliationProblems,
            StaleDocCount:          output.StaleDocCount,
            DocsWithoutHeadings:    output.DocsWithoutHeadings,
            MissingTitleCount:      output.MissingTitleCount,
            MissingVersionCount:    output.MissingVersionCount,
            MissingDepartmentCount: output.MissingDepartmentCount,
            Issues:                 output.Issues,
            RedFlags:               output.RedFlags,
            SpotCheckSample:        output.SpotCheckSample);

        return (toProcess, stats);
    }

    public (IReadOnlyList<ProtocolDocument> Docs, ChunkStats Stats) Chunk(
        IReadOnlyList<ExtractionDocument> docs) => _chunkingService.ChunkDocuments(docs);

    public async Task<EmbedUploadStats> EmbedAndUploadAsync(
        IReadOnlyList<ProtocolDocument> docs, CancellationToken ct = default)
    {
        try
        {
            var sw              = System.Diagnostics.Stopwatch.StartNew();
            var embeddingResult = await _embeddingService.EmbedDocumentsAsync(docs, ct);
            sw.Stop();

            var (succeeded, failed) = await _indexDocumentService.UpsertDocumentsAsync(embeddingResult.Documents, ct);

            Instrumentation.BlobsProcessed.Add(1, new KeyValuePair<string, object?>("status", "success"));
            _logger.LogInformation("Embedded and uploaded {Count} documents ({Succeeded} succeeded, {Failed} failed)",
                docs.Count, succeeded, failed);

            long? indexDocCount = null, indexStorageBytes = null;
            try
            {
                var (docCount, storageBytes) = await _indexService.GetStatisticsAsync(ct);
                (indexDocCount, indexStorageBytes) = (docCount, storageBytes);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Index stats snapshot failed — continuing, this run's own results are unaffected");
                Instrumentation.PipelineFailures.Add(1, new KeyValuePair<string, object?>("stage", "stats_snapshot"));
            }

            return new EmbedUploadStats(
                DocsUploaded:                  succeeded,
                DocsFailed:                    failed,
                ChunksTruncated:               embeddingResult.ChunksTruncated,
                EmbeddingRetries:              embeddingResult.EmbeddingRetries,
                VectorDimErrors:               embeddingResult.VectorDimErrors,
                TotalEmbeddingDurationMs:      sw.ElapsedMilliseconds,
                IndexDocumentCountSnapshot:    indexDocCount,
                IndexStorageSizeBytesSnapshot: indexStorageBytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Instrumentation.PipelineFailures.Add(1, new KeyValuePair<string, object?>("stage", "embed_upload"));
            Instrumentation.BlobsProcessed.Add(1, new KeyValuePair<string, object?>("status", "failure"));
            throw;
        }
    }
}
