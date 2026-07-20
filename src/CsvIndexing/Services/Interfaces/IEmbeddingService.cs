using IndexingShared.Models;

namespace CsvIndexing.Services;

public interface IEmbeddingService
{
    Task<EmbeddingRunResult> EmbedDocumentsAsync(IEnumerable<ProtocolDocument> documents, CancellationToken ct = default);
}

public record EmbeddingRunResult(
    IEnumerable<ProtocolDocument> Documents,
    int ChunksTruncated,
    int EmbeddingRetries,
    int VectorDimErrors
);
