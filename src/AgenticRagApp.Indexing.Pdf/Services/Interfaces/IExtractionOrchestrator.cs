using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// The PDF extraction pipeline (download, run Document Intelligence, clean, validate).
// Kept as an interface purely so ExtractionService's unit tests can mock it - not for
// swapping to another document type at runtime; this pipeline is PDF-only.
public interface IExtractionOrchestrator
{
    string Source { get; }  // "pdf"

    // Extracts only the given source ids - ExtractionService has already listed what's in
    // blob storage and diffed it against the index to determine these are the only ones
    // that are new or changed. Ids outside this set produce no ExtractionDocuments.
    Task<PdfExtractionOutput> ExtractDocumentsAsync(IReadOnlySet<string> sourceIdsToProcess, CancellationToken ct = default);
}
