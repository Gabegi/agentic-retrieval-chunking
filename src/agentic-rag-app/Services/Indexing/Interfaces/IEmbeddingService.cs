using AgenticRag.Models;

namespace AgenticRag.Services;

public interface IEmbeddingService
{
    Task<EmbeddingRunResult> EmbedDocumentsAsync(IEnumerable<ProtocolDocument> documents, CancellationToken ct = default);
}

public record EmbeddingRunResult(
    IEnumerable<ProtocolDocument> Documents,
    int ChunksTruncated,
    int EmbeddingRetries,
    int VectorDimErrors,
    // Chunks whose vector came from VectorCache instead of a paid embedding call.
    int CacheHits
);
