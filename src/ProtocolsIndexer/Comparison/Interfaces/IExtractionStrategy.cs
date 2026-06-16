using ProtocolsIndexer.Comparison.Models;

namespace ProtocolsIndexer.Comparison.Interfaces;

public interface IExtractionStrategy
{
    string Name { get; }
    Task<ExtractionResult> ExtractAsync(string blobName, byte[] pdfBytes, CancellationToken ct = default);
}
