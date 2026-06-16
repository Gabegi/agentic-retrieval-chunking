using Azure.Storage.Blobs.Models;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public interface IExtractionService
{
    string Name { get; }
    Task<ExtractionRun> ExtractAsync(BlobItem blob, byte[] pdfBytes, CancellationToken ct = default);
}
