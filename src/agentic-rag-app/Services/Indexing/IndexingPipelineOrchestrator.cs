using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Models;
using ProtocolsIndexer.Observability;
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
    private readonly IIndexCRUDService                           _indexCrudService;
    private readonly ILogger<IndexingPipelineOrchestrator>       _logger;

    public IndexingPipelineOrchestrator(
        IEnumerable<IExtractionOrchestrator> extractors,
        IChunkingService                     chunkingService,
        IEmbeddingService                    embeddingService,
        IIndexService                        indexService,
        IIndexCRUDService                    indexCrudService,
        ILogger<IndexingPipelineOrchestrator> logger)
    {
        _extractors       = extractors.ToDictionary(e => e.Source, StringComparer.OrdinalIgnoreCase);
        _chunkingService  = chunkingService;
        _embeddingService = embeddingService;
        _indexService     = indexService;
        _indexCrudService = indexCrudService;
        _logger           = logger;
    }

    public async Task<IReadOnlyList<ExtractionDocument>> ExtractAsync(string source, CancellationToken ct = default)
    {
        // Resolve extractor by the ?source= param (e.g. "csv"). New sources only need a new IExtractionOrchestrator registered in program.cs.
        if (!_extractors.TryGetValue(source, out var extractor))
            throw new ArgumentException(
                $"No extractor registered for source '{source}'. Available: {string.Join(", ", _extractors.Keys)}");

        await _indexService.EnsureIndexAsync();

        _logger.LogInformation("Extracting from source '{Source}'", source);
        return await extractor.ExtractAsync(ct);
    }

    public IReadOnlyList<ProtocolDocument> Chunk(IReadOnlyList<ExtractionDocument> docs)
    {
        var result           = new List<ProtocolDocument>();
        int globalChunkIndex = 0;

        foreach (var doc in docs.OrderBy(d => d.SourceId).ThenBy(d => d.Ordinal))
        {
            var chunks = _chunkingService.Chunk(doc.Content);
            foreach (var chunk in chunks)
            {
                var content = chunk.Heading != null
                    ? $"{chunk.Heading}\n\n{chunk.Content}"
                    : chunk.Content;

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

        Instrumentation.ChunksExtracted.Record(result.Count,
            new KeyValuePair<string, object?>("strategy", _chunkingService.Name));

        _logger.LogInformation("Chunked {Docs} docs into {Chunks} chunks ({Strategy})",
            docs.Count, result.Count, _chunkingService.Name);
        return result;
    }

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, out var result) ? result : null;

    public async Task EmbedAndUploadAsync(IReadOnlyList<ProtocolDocument> docs, CancellationToken ct = default)
    {
        var embedded = await _embeddingService.EmbedDocumentsAsync(docs, ct);
        await _indexCrudService.UpsertDocumentsAsync(embedded, ct);

        Instrumentation.BlobsProcessed.Add(1, new KeyValuePair<string, object?>("status", "success"));
        _logger.LogInformation("Embedded and uploaded {Count} documents", docs.Count);
    }
}
