using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Indexing.Pdf.Models;

public record ExtractionOutput(
    IReadOnlyList<ExtractionDocument>  Docs,
    int                                ValidationErrors,
    int                                ValidationWarnings,
    int                                ReconciliationProblems,
    int?                               StaleDocCount,          // null = source has no equivalent concept, not "verified zero"
    int                                MojibakeRepairedPages,
    int                                DetectedTableCount,
    int                                DocsWithoutHeadings,
    int                                MissingTitleCount,
    int?                               MissingVersionCount,    // PDF: real count now (ZenyaVersion missing) - see BuildExtractionOutput
    int?                               MissingDepartmentCount, // null = source has no equivalent concept, not "verified zero"
    // Documents with no zenya_document_id blob metadata set - the same gap
    // Citation.TraceabilityGap flags per-citation at query time, aggregated here so a
    // whole-run count is visible without waiting for a query to surface one. Nullable for
    // the same reason as MissingVersionCount/MissingDepartmentCount above: CSV has its own,
    // different traceability field (relative_path) and doesn't compute this one - null there
    // means "not this source's mechanism," not "verified zero".
    int?                               TraceabilityGapCount,
    IReadOnlyList<ValidationIssueEntry> Issues,
    IReadOnlyList<string>              RedFlags,
    IReadOnlyList<SpotCheckEntry>      SpotCheckSample
);
