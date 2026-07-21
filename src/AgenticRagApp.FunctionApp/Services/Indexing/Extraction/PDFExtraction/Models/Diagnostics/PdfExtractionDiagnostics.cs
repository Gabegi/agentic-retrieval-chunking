namespace AgenticRagApp.Models;

// Snapshot of what each extraction step actually produced for one PDF. Currently always
// null on every PdfFileExtraction - the only producer was the PdfPig backend, which has
// been removed. Left in place as a report slot (gated behind IRunReportWriter.IsEnabled
// in PdfExtractionOrchestrator) for whichever backend picks this reporting back up.
public sealed class PdfExtractionDiagnostics
{
    public required string BlobName { get; init; }

    // Step 1: document-level baseline
    public double DominantFontSize     { get; init; }
    public double DominantPageWidth    { get; init; }
    public int    KnownSectionCount    { get; init; }
    public bool   BookmarksContributed { get; init; } // true if the PDF's own outline added headings beyond the hardcoded vocabulary

    // Step 2: cross-page structural analysis
    public bool DecorationDetectionRan     { get; init; } // false for docs under MinPagesForDecorationDetection
    public int  PagesWithDecorationRemoved { get; init; }

    // Step 3: document metadata - currently unused; only the removed PdfPig backend
    // ever populated these.
    public string? ParsedTitle              { get; init; }
    public string? ParsedVersion            { get; init; }
    public string? ParsedPublicationDateRaw { get; init; }

    // Overall outcome
    public int PageCount      { get; init; }
    public int PageErrorCount { get; init; }
    public int WarningCount   { get; init; }
}
