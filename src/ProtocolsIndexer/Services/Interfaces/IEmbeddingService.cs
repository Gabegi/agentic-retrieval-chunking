using InvoiceIndexer.Models;

public interface IEmbeddingService
{
    Task<IEnumerable<InvoiceDocument>> EmbedDocumentsAsync(IEnumerable<InvoiceDocument> documents, CancellationToken ct = default);
    Task UploadDocumentsAsync(IEnumerable<InvoiceDocument> documents, CancellationToken ct = default);
}