namespace AgenticRagApp.Observability.Reports;

// Minimal shape ChunkingResults.Compute needs from a chunk, regardless of which doc-type
// pipeline produced it. Implemented by each pipeline's own chunk type (e.g. DocumentChunk) —
// Observability never references those types directly.
public interface IChunkStatsSource
{
    string DocumentId { get; }
    string Content { get; }
    bool   IsCoherent { get; }
    string? Heading { get; }
}
