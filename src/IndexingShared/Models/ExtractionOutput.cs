using AgenticRagApp.Observability.Reports;

namespace IndexingShared.Models;

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
    int?                               MissingVersionCount,    // null = source has no equivalent concept, not "verified zero"
    int?                               MissingDepartmentCount, // null = source has no equivalent concept, not "verified zero"
    IReadOnlyList<ValidationIssueEntry> Issues,
    IReadOnlyList<string>              RedFlags,
    IReadOnlyList<SpotCheckEntry>      SpotCheckSample
);
