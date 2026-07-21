using AgenticRag.Models;
using AgenticRag.Observability.Reports;

namespace AgenticRag.Services;

public interface IChunkingService
{
    string Name { get; }

    // Low-level: splits raw text into TextChunks using the configured strategy.
    IReadOnlyList<TextChunk> Chunk(string content);

    // High-level: converts ExtractionDocuments into indexed DocumentChunks,
    // computes ChunkingResults, and emits all chunk telemetry.
    (IReadOnlyList<DocumentChunk> Docs, ChunkingResults Stats) ChunkDocuments(
        IReadOnlyList<ExtractionDocument> docs);
}
