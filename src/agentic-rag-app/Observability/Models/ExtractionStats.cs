using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Observability.Reports;

public record ExtractionResults(
    int DocsToProcess,
    int DocsSkipped,
    int DocsNew,
    int DocsUpdated,
    int DocsDeleted,
    // Document IDs (updated + removed) whose stale chunks still need cleanup. Carried forward
    // to EmbedAndUploadActivity rather than deleted here - see ExtractionService.ExtractAndDiffAsync.
    IReadOnlyList<string> StaleDocumentIds,
    int ValidationErrors,
    int ValidationWarnings,
    int ReconciliationProblems,
    int StaleDocCount,
    int DocsWithoutHeadings,
    int MissingTitleCount,
    int MissingVersionCount,
    int MissingDepartmentCount,
    IReadOnlyList<ValidationIssueEntry> Issues,
    IReadOnlyList<string>               RedFlags,
    IReadOnlyList<SpotCheckEntry>       SpotCheckSample
);
