namespace AgenticRagApp.Observability.Reports;

// Same shape as PdfIndexRunReport (IndexRunReportBase) plus three CSV-only fields that
// always have real values for CSV, unlike PDF where they have no equivalent concept at all.
public sealed record CsvIndexRunReport : IndexRunReportBase
{
    // Quality signal: docs past their check_date — live but potentially stale guidance in
    // the index. Retrieval will surface it as if it were current — flag to content owners.
    public required int StaleDocCount { get; init; }
    public required int MissingVersionCount { get; init; }
    public required int MissingDepartmentCount { get; init; }
}
