namespace ProtocolsIndexer.Models;

public class ValidationReport
{
    public DateTime RunAtUtc              { get; init; }
    public int      PagesExtracted        { get; init; }
    public int      IndexRecordsExtracted { get; init; }
    public int      JoinedRecords         { get; init; }
    public int      CleanedRecords        { get; init; }
    public bool     Passed                { get; init; }

    public IReadOnlyList<ValidationIssue>   Issues                        { get; init; } = [];
    public IReadOnlyList<string>            ReconciliationProblems        { get; init; } = [];
    public IReadOnlyList<string>            MagnitudeWarnings             { get; init; } = [];
    public IReadOnlyList<string>            RedFlags                      { get; init; } = [];
    public IReadOnlyList<CleanedPageRecord> SpotCheckSample               { get; init; } = [];
    public IReadOnlyList<string>            DocumentsNeedingFallbackChunking { get; init; } = [];
    public IReadOnlyList<string>            SkippedIndexDocuments             { get; init; } = [];  // "DocumentTypeName (DocumentId)" of index docs with no pages
    public int                              StaleDocCount                     { get; init; }
}
