using ProtocolsIndexer.Models;

public interface IEmbeddingService
{
    Task<IEnumerable<ProtocolDocument>> EmbedDocumentsAsync(IEnumerable<ProtocolDocument> documents, CancellationToken ct = default);
    Task UploadDocumentsAsync(IEnumerable<ProtocolDocument> documents, CancellationToken ct = default);
}
