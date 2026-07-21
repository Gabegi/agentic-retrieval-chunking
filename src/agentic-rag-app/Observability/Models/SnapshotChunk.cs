using AgenticRag.Models;

namespace AgenticRag.Observability.Reports;

// One row per chunk believed to be live in the Search index right now. The rolling snapshot
// (indexing-artifacts/snapshots/{instanceId}/full-index.json) is the union of these across
// every run, not a per-run diff. Carries everything a future rebuild would need to bulk-upsert
// directly into a fresh index - the real fields UploadService sends to Search, plus
// ContentHash so the vector can be resolved from VectorCache without re-embedding.
public record SnapshotChunk(
    string Id,
    string DocumentId,
    string? Title,
    string? Department,
    string? QuickCode,
    string? RelativePath,
    DateTimeOffset? LastModifiedDate,
    DateTimeOffset? CheckDate,
    string? Version,
    string Content,
    string? Summary,
    string? Heading,
    int PageNumber,
    int ChunkIndex,
    string ContentHash)
{
    public static SnapshotChunk From(DocumentChunk doc) => new(
        Id:               doc.Id,
        DocumentId:       doc.DocumentId,
        Title:            doc.Title,
        Department:       doc.Department,
        QuickCode:        doc.QuickCode,
        RelativePath:     doc.RelativePath,
        LastModifiedDate: doc.LastModifiedDate,
        CheckDate:        doc.CheckDate,
        Version:          doc.Version,
        Content:          doc.Content,
        Summary:          doc.Summary,
        Heading:          doc.Heading,
        PageNumber:       doc.PageNumber,
        ChunkIndex:       doc.ChunkIndex,
        ContentHash:      doc.ContentHash);
}
