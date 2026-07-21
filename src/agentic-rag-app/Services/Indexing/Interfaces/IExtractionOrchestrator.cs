using AgenticRag.Models;

namespace AgenticRag.Services;

// Any extractor (CSV, PDF, …) implements this and declares its source key.
// Register multiple implementations in DI — IndexingPipelineOrchestrator resolves by Source at runtime.
public interface IExtractionOrchestrator
{
    string Source { get; }  // e.g. "csv", "pdf"

    // Cheap listing of every source document currently available to extract - id (blob name
    // for PDF) + LastModified only, no download or extraction. Lets ExtractionService diff
    // against the index BEFORE paying for extraction on anything already up to date.
    Task<IReadOnlyDictionary<string, DateTimeOffset>> ListSourceDocumentsAsync(CancellationToken ct = default);

    // Extracts only the given source ids - ExtractionService has already diffed
    // ListSourceDocumentsAsync's listing against the index and determined these are the
    // only ones that are new or changed. Ids outside this set produce no ExtractionDocuments.
    Task<ExtractionOutput> ExtractDocumentsAsync(IReadOnlySet<string> sourceIdsToProcess, CancellationToken ct = default);
}
