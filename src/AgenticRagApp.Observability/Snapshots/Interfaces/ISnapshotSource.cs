namespace AgenticRagApp.Observability.Reports;

// Minimal shape SnapshotChunk.From needs from a chunk, regardless of which doc-type
// pipeline produced it. Implemented by each pipeline's own chunk type (e.g. DocumentChunk) —
// Observability never references those types directly.
public interface ISnapshotSource
{
    string Id { get; }
    string DocumentId { get; }
    string? Title { get; }
    DateTimeOffset? LastModifiedDate { get; }
    string Content { get; }
    string? Heading { get; }
    int PageNumber { get; }
    int ChunkIndex { get; }
    string ContentHash { get; }
}
