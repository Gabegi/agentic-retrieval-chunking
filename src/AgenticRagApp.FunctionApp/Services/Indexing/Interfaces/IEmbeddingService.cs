using AgenticRagApp.Models;

namespace AgenticRagApp.Services;

public interface IEmbeddingService
{
    Task<EmbeddingRunResult> EmbedDocumentsAsync(IEnumerable<DocumentChunk> documents, CancellationToken ct = default);
}

public record EmbeddingRunResult(
    IEnumerable<DocumentChunk> Documents,
    int ChunksTruncated,
    int EmbeddingRetries,
    int VectorDimErrors,
    // Chunks whose vector came from VectorCache instead of a paid embedding call.
    int CacheHits
);
