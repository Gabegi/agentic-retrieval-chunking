using CsvIndexing.Models;
using IndexingShared.Models;
using AgenticRagApp.Observability.Reports;

namespace CsvIndexing.Services;

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
