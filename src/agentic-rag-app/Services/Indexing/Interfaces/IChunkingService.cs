using ProtocolsIndexer.Models;
using ProtocolsIndexer.Observability.Reports;

namespace ProtocolsIndexer.Services;

public interface IChunkingService
{
    string Name { get; }

    // Low-level: splits raw text into TextChunks using the configured strategy.
    IReadOnlyList<TextChunk> Chunk(string content);

    // High-level: converts ExtractionDocuments into indexed ProtocolDocuments,
    // computes ChunkingResults, and emits all chunk telemetry.
    (IReadOnlyList<ProtocolDocument> Docs, ChunkingResults Stats) ChunkDocuments(
        IReadOnlyList<ExtractionDocument> docs);
}
