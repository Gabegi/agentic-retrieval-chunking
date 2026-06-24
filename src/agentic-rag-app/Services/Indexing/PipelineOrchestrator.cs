using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Models;
using ProtocolsIndexer.Utils;

namespace ProtocolsIndexer.Services;

// Source-agnostic RAG pipeline: resolves the right extractor by source key,
// then chunks, embeds, and uploads. Adding a new source means registering
// a new IExtractionOrchestrator — no changes here.
public class RagPipelineOrchestrator : IRagPipelineOrchestrator
{
    private readonly Dictionary<string, IExtractionOrchestrator> _extractors;
    private readonly IChunkingService                            _chunkingService;
    private readonly IEmbeddingService                           _embeddingService;
    private readonly IIndexService                               _indexService;
    private readonly ILogger<RagPipelineOrchestrator>            _logger;

    private volatile bool _indexEnsured;

    public RagPipelineOrchestrator(
        IEnumerable<IExtractionOrchestrator> extractors,
        IChunkingService                     chunkingService,
        IEmbeddingService                    embeddingService,
        IIndexService                        indexService,
        ILogger<RagPipelineOrchestrator>     logger)
    {
        _extractors       = extractors.ToDictionary(e => e.Source, StringComparer.OrdinalIgnoreCase);
        _chunkingService  = chunkingService;
        _embeddingService = embeddingService;
        _indexService     = indexService;
        _logger           = logger;
    }

    public async Task<IReadOnlyList<ExtractionDocument>> ExtractAsync(string source, CancellationToken ct = default)
    {
        if (!_extractors.TryGetValue(source, out var extractor))
            throw new ArgumentException(
                $"No extractor registered for source '{source}'. Available: {string.Join(", ", _extractors.Keys)}");

        if (!_indexEnsured)
        {
            await _indexService.EnsureIndexAsync();
            _indexEnsured = true;
        }

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
                    Id              = ChunkingUtils.SafeKey($"{doc.SourceId}::{doc.Ordinal}", globalChunkIndex),
                    SourceFile      = doc.SourceId,
                    RichtlijnName   = doc.Metadata.GetValueOrDefault("title"),
                    PublicationDate = doc.Metadata.GetValueOrDefault("publication_date"),
                    Version         = doc.Metadata.GetValueOrDefault("version"),
                    Content         = content,
                    Heading         = chunk.Heading,
                    PageNumber      = doc.Ordinal,
                    ChunkIndex      = globalChunkIndex++,
                });
            }
        }

        _logger.LogInformation("Chunked {Docs} docs into {Chunks} chunks ({Strategy})",
            docs.Count, result.Count, _chunkingService.Name);
        return result;
    }

    public async Task EmbedAndUploadAsync(IReadOnlyList<ProtocolDocument> docs, CancellationToken ct = default)
    {
        var embedded = await _embeddingService.EmbedDocumentsAsync(docs, ct);
        await _embeddingService.UploadDocumentsAsync(embedded, ct);
        _logger.LogInformation("Embedded and uploaded {Count} documents", docs.Count);
    }
}
