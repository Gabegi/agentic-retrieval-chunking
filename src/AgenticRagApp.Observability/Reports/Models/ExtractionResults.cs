namespace AgenticRagApp.Observability.Reports;

public record ExtractionResults(
    // Which extractor ran (IExtractionOrchestrator.Source, e.g. "csv") - reported here rather
    // than accepted as caller input, since exactly one extractor is registered at a time.
    string Source,
    int DocsToProcess,
    int DocsSkipped,
    int DocsNew,
    int DocsUpdated,
    int DocsDeleted,
    // Document IDs (updated + removed) whose stale chunks still need cleanup. Carried forward
    // to EmbedAndUploadActivity rather than deleted here - see ExtractionService.ExtractAsync.
    IReadOnlyList<string> StaleDocumentIds,
    int ValidationErrors,
    int ValidationWarnings,
    int ReconciliationProblems,
    int? StaleDocCount,          // null = source has no equivalent concept, not "verified zero"
    int MojibakeRepairedPages,
    int DetectedTableCount,
    int DocsWithoutHeadings,
    int MissingTitleCount,
    int? MissingVersionCount,    // PDF: real count now (ZenyaVersion missing) - see PdfExtractionPipeline
    int? MissingDepartmentCount, // null = source has no equivalent concept, not "verified zero"
    // Documents with no zenya_document_id blob metadata set - see ExtractionOutput's own comment.
    int? TraceabilityGapCount,
    IReadOnlyList<ValidationIssueEntry> Issues,
    IReadOnlyList<string>               RedFlags,
    IReadOnlyList<SpotCheckEntry>       SpotCheckSample
);
