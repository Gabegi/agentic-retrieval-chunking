using ProtocolsIndexer.Models;
using ProtocolsIndexer.Observability.Reports;

namespace ProtocolsIndexer.Services;

public interface IChunkingService
{
    string Name { get; }

    // Low-level: splits raw text into TextChunks using the configured strategy.
    IReadOnlyList<TextChunk> ChunkAsync(string content);

    // High-level: converts ExtractionDocuments into indexed ProtocolDocuments,
    // computes ChunkStats, and emits all chunk telemetry.
    (IReadOnlyList<ProtocolDocument> Docs, ChunkStats Stats) ChunkDocuments(
        IReadOnlyList<ExtractionDocument> docs);
}
