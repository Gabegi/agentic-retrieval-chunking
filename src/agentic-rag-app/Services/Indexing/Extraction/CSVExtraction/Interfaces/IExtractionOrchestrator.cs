using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// Any extractor (CSV, PDF, …) implements this and declares its source key.
// Register multiple implementations in DI — IndexingPipelineOrchestrator resolves by Source at runtime.
public interface IExtractionOrchestrator
{
    string Source { get; }  // e.g. "csv", "pdf"

    // overrideMagnitudeCheck: bypasses ONLY the magnitude-shift validation gate (a large,
    // legitimate import/removal), never the error-rate or reconciliation checks - those
    // indicate genuinely malformed data and must always block.
    Task<ExtractionOutput> ExtractDocumentsAsync(bool overrideMagnitudeCheck = false, CancellationToken ct = default);
}
