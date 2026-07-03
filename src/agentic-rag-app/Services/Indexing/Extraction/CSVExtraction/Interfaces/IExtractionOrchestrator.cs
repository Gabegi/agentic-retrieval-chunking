using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// Any extractor (CSV, PDF, …) implements this and declares its source key.
// Register multiple implementations in DI — IndexingPipelineOrchestrator resolves by Source at runtime.
public interface IExtractionOrchestrator
{
    string Source { get; }  // e.g. "csv", "pdf"
    Task<ExtractionOutput> ExtractDocumentsAsync(CancellationToken ct = default);
}
