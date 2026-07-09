namespace ProtocolsIndexer.Models;

public record ExtractionOutput(
    IReadOnlyList<ExtractionDocument>  Docs,
    int                                ValidationErrors,
    int                                ValidationWarnings,
    int                                ReconciliationProblems,
    int                                StaleDocCount,
    int                                MojibakeRepairedPages,
    int                                DetectedTableCount,
    int                                DocsWithoutHeadings,
    int                                MissingTitleCount,
    int                                MissingVersionCount,
    int                                MissingDepartmentCount,
    IReadOnlyList<ValidationIssueEntry> Issues,
    IReadOnlyList<string>              RedFlags,
    IReadOnlyList<SpotCheckEntry>      SpotCheckSample
);

public record ValidationIssueEntry(string Stage, string Severity, string DocumentId, string Message);

// Brief content preview used for manual spot-checking in the dev run report.
public record SpotCheckEntry(string DocumentId, string Title, string ContentPreview);
