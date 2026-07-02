using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Models;
using ProtocolsIndexer.Observability;
using ProtocolsIndexer.Observability.Reports;
using ProtocolsIndexer.Utils;

namespace ProtocolsIndexer.Services;

public class ChunkingService : IChunkingService
{
    private readonly IChunkingStrategy             _strategy;
    private readonly ILogger<ChunkingService>      _logger;

    public string Name => _strategy.Name;

    public ChunkingService(IChunkingStrategy strategy, ILogger<ChunkingService> logger)
    {
        _strategy = strategy;
        _logger   = logger;
    }

    // Low-level passthrough — splits raw text into TextChunks.
    public IReadOnlyList<TextChunk> ChunkAsync(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        return _strategy.Chunk(content);
    }

    // Converts ExtractionDocuments into indexed ProtocolDocuments,
    // computes ChunkingResults, and emits all chunk telemetry in one place.
    public (IReadOnlyList<ProtocolDocument> Docs, ChunkingResults Stats) ChunkDocuments(
        IReadOnlyList<ExtractionDocument> docs)
    {
        var result           = new List<ProtocolDocument>();
        int globalChunkIndex = 0;
        string? previousSourceId = null;

        foreach (var doc in docs.OrderBy(d => d.SourceId).ThenBy(d => d.Ordinal))
        {
            var title       = doc.Metadata.GetValueOrDefault("title") ?? "";
            var summary     = doc.Metadata.GetValueOrDefault("summary") ?? "";
            // Group-transition check rather than doc.Ordinal == 0 — nothing guarantees
            // PAGE_INDEX is 0-based in the source CSV, so this works regardless of the
            // actual starting value.
            var isFirstPage = doc.SourceId != previousSourceId;
            previousSourceId = doc.SourceId;
            var chunks = ChunkAsync(doc.Content);

            foreach (var chunk in chunks)
            {
                // Prepend the document title so every chunk — including short continuation
                // pages with no query-term overlap on their own — benefits from the parent
                // document's identity in both BM25 and vector scoring.
                var body = chunk.Heading != null ? $"{chunk.Heading}\n\n{chunk.Content}" : chunk.Content;

                // The Zenya summary is a curated "what is this document for" line — prime
                // semantic-ranking material, but once per document is enough.
                var header = isFirstPage && chunk.Index == 0 && !string.IsNullOrWhiteSpace(summary)
                    ? $"{title}\n{summary}"
                    : title;

                var content = string.IsNullOrEmpty(header) ? body : $"{header}\n\n{body}";

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

        var stats = ChunkingResults.Compute(result, Name);
        EmitChunkMetrics(stats, result);

        _logger.LogInformation("Chunked {Docs} docs into {Chunks} chunks ({Strategy})",
            docs.Count, result.Count, stats.Strategy);

        return (result, stats);
    }

    private static void EmitChunkMetrics(ChunkingResults stats, IReadOnlyList<ProtocolDocument> chunks)
    {
        var strategyTag = new KeyValuePair<string, object?>("strategy", stats.Strategy);

        Instrumentation.ChunksExtracted.Record(stats.ChunksProduced, strategyTag);

        // Per-chunk histogram — preserves the real distribution in App Insights,
        // not just the aggregates already in ChunkingResults.
        foreach (var chunk in chunks)
            Instrumentation.ChunkSizeChars.Record(chunk.Content.Length, strategyTag);

        Instrumentation.ChunkSizeBand.Add(stats.BandUnder100,  strategyTag, new("band", "under_100"));
        Instrumentation.ChunkSizeBand.Add(stats.Band100To500,  strategyTag, new("band", "100_to_500"));
        Instrumentation.ChunkSizeBand.Add(stats.Band500To1500, strategyTag, new("band", "500_to_1500"));
        Instrumentation.ChunkSizeBand.Add(stats.Band1500Plus,  strategyTag, new("band", "1500_plus"));

        // All quality counters now carry strategyTag consistently.
        Instrumentation.DuplicateChunks.Add(stats.DuplicateChunks,   strategyTag);
        Instrumentation.CoherentChunks.Add(stats.CoherentChunks,     strategyTag);
        Instrumentation.HeadingsDetected.Add(stats.HeadingsDetected, strategyTag);

        if (stats.DocsWithZeroChunks > 0)
            Instrumentation.DocsWithZeroChunks.Add(stats.DocsWithZeroChunks, strategyTag);
    }

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, out var result) ? result : null;
}
