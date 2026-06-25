using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// Any extractor (CSV, PDF, …) implements this and declares its source key.
// Register multiple implementations in DI — RagPipelineOrchestrator resolves by Source at runtime.
public interface IExtractionOrchestrator
{
    string Source { get; }  // e.g. "csv", "pdf"
    Task<IReadOnlyList<ExtractionDocument>> ExtractDocumentsAsync(CancellationToken ct = default);
}
