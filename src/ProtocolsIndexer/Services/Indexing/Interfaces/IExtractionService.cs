using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public interface IExtractionService
{
    string Name { get; }
    Task<ExtractionRun> ExtractAsync(string blobName, byte[] pdfBytes, CancellationToken ct = default);
}
