using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public interface IIndexDocumentService
{
    Task<Dictionary<string, DateTimeOffset>> GetIndexedDocumentDatesAsync(CancellationToken ct = default);
    Task<(int Succeeded, int Failed)> UpsertDocumentsAsync(IEnumerable<ProtocolDocument> documents, CancellationToken ct = default);
    Task<int> DeleteDocumentsAsync(IEnumerable<string> documentIds, CancellationToken ct = default);
}
