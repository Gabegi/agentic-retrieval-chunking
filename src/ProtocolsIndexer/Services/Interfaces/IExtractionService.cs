using Azure.Storage.Blobs.Models;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public interface IExtractionService
{
    Task<IEnumerable<BlobItem>> ReadBlobsAsync(CancellationToken ct = default);
    Task<IEnumerable<ProtocolDocument>> ExtractDocumentsAsync(IEnumerable<BlobItem> blobs, CancellationToken ct = default);
}
