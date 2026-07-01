using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// Per-file PDF extractor shape used only by ExtractionComparisonRunner to A/B
// different PDF extraction strategies. Distinct from the pipeline-level IExtractionService.
public interface IPdfExtractionService
{
    string Name { get; }
    Task<ExtractionRun> ExtractAsync(string blobName, byte[] pdfBytes, CancellationToken ct = default);
}
