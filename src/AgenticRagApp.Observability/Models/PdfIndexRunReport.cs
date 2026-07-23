namespace AgenticRagApp.Observability.Reports;

// Same shape as CsvIndexRunReport (IndexRunReportBase). Deliberately has no
// StaleDocCount/MissingVersionCount/MissingDepartmentCount - PDF has no equivalent
// concept for any of them (unlike CsvIndexRunReport, which always has real values).
//
// How to use: compare two reports side-by-side after a source change or config tweak
// to see whether quality moved in the right direction.
public sealed record PdfIndexRunReport : IndexRunReportBase
{
    // Quality signal: documents with no zenya_document_id blob metadata set. Non-zero means
    // every citation built from them will show Citation.TraceabilityGap - this is the one
    // metric that tells you, without waiting for a query, how much of the corpus is
    // currently untraceable back to Zenya. Expected to be the full corpus count until
    // whoever uploads PDFs starts setting this metadata.
    public int? TraceabilityGapCount { get; init; }
}
