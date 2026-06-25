using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public interface IEmbeddingService
{
    Task<IEnumerable<ProtocolDocument>> EmbedDocumentsAsync(IEnumerable<ProtocolDocument> documents, CancellationToken ct = default);
}
