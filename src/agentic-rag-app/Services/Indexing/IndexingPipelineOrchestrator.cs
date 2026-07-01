using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Models;
using ProtocolsIndexer.Observability;
using ProtocolsIndexer.Observability.Reports;
using ProtocolsIndexer.Utils;

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
        IEnumerable<IExtractionOrchestrator> extractors,
        IChunkingService                     chunkingService,
        IEmbeddingService                    embeddingService,
        IIndexService                        indexService,
        IIndexDocumentService                indexDocumentService,
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
                continue; // unchanged — skip

            toDelete.Add(doc.SourceId);
            toProcess.Add(doc);
            updated++;
        }

        if (toDelete.Count > 0)
        {
            _logger.LogInformation("Deleting stale chunks for {Count} changed documents", toDelete.Count);
            await _indexDocumentService.DeleteDocumentsAsync(toDelete, ct);
        }

        var skipped = output.Docs.Count - toProcess.Count;
        _logger.LogInformation("Skipping {Skipped} unchanged, processing {Count} new/changed documents",
            skipped, toProcess.Count);

        var sourceTag = new KeyValuePair<string, object?>("source", source);
        Instrumentation.DocsExtracted.Add(output.Docs.Count, sourceTag);
        Instrumentation.DocsSkipped.Add(skipped,        sourceTag);
        Instrumentation.DocsNew.Add(newCount,            sourceTag);
        Instrumentation.DocsUpdated.Add(updated,         sourceTag);
        Instrumentation.DocsDeleted.Add(toDelete.Count,  sourceTag);

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

    public (IReadOnlyList<ProtocolDocument> Docs, ChunkStats Stats) Chunk(IReadOnlyList<ExtractionDocument> docs)
    {
        var result           = new List<ProtocolDocument>();
        var seen             = new HashSet<string>();
        int globalChunkIndex = 0;

        foreach (var doc in docs.OrderBy(d => d.SourceId).ThenBy(d => d.Ordinal))
        {
            var title  = doc.Metadata.GetValueOrDefault("title") ?? "";
            var chunks = _chunkingService.ChunkAsync(doc.Content);
            foreach (var chunk in chunks)
            {
                var body = chunk.Heading != null
                    ? $"{chunk.Heading}\n\n{chunk.Content}"
                    : chunk.Content;
                var content = string.IsNullOrEmpty(title) ? body : $"{title}\n\n{body}";

                result.Add(new ProtocolDocument
                {
                    Id               = ChunkingUtils.SafeKey($"{doc.SourceId}::{doc.Ordinal}", globalChunkIndex),
                    DocumentId       = doc.SourceId,
                    Title            = doc.Metadata.GetValueOrDefault("title"),
                    Department       = doc.Metadata.GetValueOrDefault("folder_path"),
                    QuickCode        = doc.Metadata.GetValueOrDefault("quick_code"),
                    LastModifiedDate = ParseDate(doc.Metadata.GetValueOrDefault("last_modified_date")),
                    CheckDate        = ParseDate(doc.Metadata.GetValueOrDefault("check_date")),
                    Version          = doc.Metadata.GetValueOrDefault("version"),
                    Content          = content,
                    Heading          = chunk.Heading,
                    PageNumber       = doc.Ordinal,
                    ChunkIndex       = globalChunkIndex++,
                });
            }
        }

        var stats = ComputeChunkStats(result, seen);

        Instrumentation.ChunksExtracted.Record(result.Count,
            new KeyValuePair<string, object?>("strategy", _chunkingService.Name));

        _logger.LogInformation("Chunked {Docs} docs into {Chunks} chunks ({Strategy})",
            docs.Count, result.Count, _chunkingService.Name);

        return (result, stats);
    }

    private ChunkStats ComputeChunkStats(List<ProtocolDocument> chunks, HashSet<string> seen)
    {
        if (chunks.Count == 0)
            return new ChunkStats(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

        var strategyTag = new KeyValuePair<string, object?>("strategy", _chunkingService.Name);

        var sizes          = new List<long>(chunks.Count);
        var docsProduced   = new HashSet<string>();
        var allDocIds      = new HashSet<string>(chunks.Select(c => c.DocumentId));
        var duplicates     = 0;
        var oversized      = 0;
        var undersized     = 0;
        var coherent       = 0;
        var headings       = 0;
        var totalTokens    = 0L;
        var band0          = 0;
        var band1          = 0;
        var band2          = 0;
        var band3          = 0;

        foreach (var chunk in chunks)
        {
            var len = (long)chunk.Content.Length;
            sizes.Add(len);
            docsProduced.Add(chunk.DocumentId);

            // Size bands
            if      (len < 100)  { band0++; Instrumentation.ChunkSizeBand.Add(1, strategyTag, new("band", "under_100")); }
            else if (len < 500)  { band1++; Instrumentation.ChunkSizeBand.Add(1, strategyTag, new("band", "100_to_500")); }
            else if (len < 1500) { band2++; Instrumentation.ChunkSizeBand.Add(1, strategyTag, new("band", "500_to_1500")); }
            else                 { band3++; Instrumentation.ChunkSizeBand.Add(1, strategyTag, new("band", "1500_plus")); }

            Instrumentation.ChunkSizeChars.Record(len, strategyTag);

            // Duplicates (exact content match)
            if (!seen.Add(chunk.Content))
            {
                duplicates++;
                Instrumentation.DuplicateChunks.Add(1, strategyTag);
            }

            // Token-based quality
            var tokens = chunk.TokenEstimate;
            totalTokens += tokens;
            if (chunk.IsOversized)  { oversized++;  Instrumentation.OversizedChunks.Add(1); }
            if (chunk.IsUndersized) { undersized++;  Instrumentation.UndersizedChunks.Add(1); }
            if (chunk.IsCoherent)   { coherent++;    Instrumentation.CoherentChunks.Add(1); }
            if (chunk.Heading != null) { headings++; Instrumentation.HeadingsDetected.Add(1); }
        }

        var docsWithZero = allDocIds.Count - docsProduced.Count;
        if (docsWithZero > 0) Instrumentation.DocsWithZeroChunks.Add(docsWithZero, strategyTag);

        sizes.Sort();
        var p95Index = (int)(sizes.Count * 0.95);

        return new ChunkStats(
            ChunksProduced:    chunks.Count,
            DocsWithZeroChunks: docsWithZero,
            DuplicateChunks:   duplicates,
            MinChunkSizeChars: sizes[0],
            MaxChunkSizeChars: sizes[^1],
            AvgChunkSizeChars: sizes.Average(),
            P95ChunkSizeChars: sizes[Math.Min(p95Index, sizes.Count - 1)],
            BandUnder100:      band0,
            Band100To500:      band1,
            Band500To1500:     band2,
            Band1500Plus:      band3,
            OversizedChunks:   oversized,
            UndersizedChunks:  undersized,
            AvgTokenEstimate:  (double)totalTokens / chunks.Count,
            CoherentChunks:    coherent,
            HeadingsDetected:  headings);
    }

    public async Task<EmbedUploadStats> EmbedAndUploadAsync(IReadOnlyList<ProtocolDocument> docs, CancellationToken ct = default)
    {
        try
        {
            var embeddingResult = await _embeddingService.EmbedDocumentsAsync(docs, ct);
            var (succeeded, failed) = await _indexDocumentService.UpsertDocumentsAsync(embeddingResult.Documents, ct);
            var (docCount, storageBytes) = await _indexService.GetStatisticsAsync(ct);

            Instrumentation.BlobsProcessed.Add(1, new KeyValuePair<string, object?>("status", "success"));
            _logger.LogInformation("Embedded and uploaded {Count} documents", docs.Count);

            return new EmbedUploadStats(
                DocsUploaded:           succeeded,
                DocsFailed:             failed,
                ChunksTruncated:        embeddingResult.ChunksTruncated,
                EmbeddingRetries:       embeddingResult.EmbeddingRetries,
                VectorDimErrors:        embeddingResult.VectorDimErrors,
                TotalEmbeddingDurationMs: embeddingResult.TotalDurationMs,
                IndexDocumentCount:     docCount,
                IndexStorageSizeBytes:  storageBytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Instrumentation.UploadFailures.Add(1);
            throw;
        }
    }

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, out var result) ? result : null;
}
