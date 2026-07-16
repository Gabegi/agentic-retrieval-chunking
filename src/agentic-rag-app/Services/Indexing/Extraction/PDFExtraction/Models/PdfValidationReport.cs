namespace ProtocolsIndexer.Models;

// Mirrors CSV's ValidationReport. Drops StaleDocCount — Zenya's check_date_exceeded
// attention flag has no PDF equivalent (no attention-flags data source for PDFs).
// Reuses the existing (source-agnostic) ValidationIssue type.
public class PdfValidationReport
{
    public DateTime RunAtUtc              { get; init; }
    public int      PagesExtracted        { get; init; }
    public int      CleanedRecords        { get; init; }
    public bool     Passed                { get; init; }
    // Same rationale as CSV's ValidationReport.PassedExcludingMagnitude — lets a
    // caller allow an operator override past the magnitude gate only, never past
    // error-rate/reconciliation.
    public bool     PassedExcludingMagnitude { get; init; }

    public IReadOnlyList<ValidationIssue>      Issues                           { get; init; } = [];
    public IReadOnlyList<string>               ReconciliationProblems           { get; init; } = [];
    public IReadOnlyList<string>               MagnitudeWarnings                { get; init; } = [];
    public IReadOnlyList<string>               RedFlags                         { get; init; } = [];
    public IReadOnlyList<CleanedPdfPageRecord> SpotCheckSample                  { get; init; } = [];
    public IReadOnlyList<string>               DocumentsNeedingFallbackChunking { get; init; } = [];
    public int                                 MojibakeRepairedPages            { get; init; }
    public int                                 DetectedTableCount               { get; init; }
}
