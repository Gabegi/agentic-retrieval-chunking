namespace ProtocolsIndexer.Models;

// Snapshot of what each extraction step actually produced for one PDF. Built
// unconditionally by PdfPigExtractor (it's just the scalars/counts each step already
// computes - no extra work, no I/O), but only ever written out as a report by the
// orchestrator, gated behind IRunReportWriter.IsEnabled - extractors stay I/O-free,
// orchestrators own reporting (see PdfExtractionOrchestrator).
public sealed class PdfExtractionDiagnostics
{
    public required string BlobName { get; init; }

    // Step 1: document-level baseline (PdfPigExtractor.ComputeDocumentBaseline)
    public double DominantFontSize     { get; init; }
    public double DominantPageWidth    { get; init; }
    public int    KnownSectionCount    { get; init; }
    public bool   BookmarksContributed { get; init; } // true if the PDF's own outline added headings beyond the hardcoded vocabulary

    // Step 2: cross-page structural analysis (PdfPigExtractor.GetDecorationTextByPage)
    public bool DecorationDetectionRan     { get; init; } // false for docs under MinPagesForDecorationDetection
    public int  PagesWithDecorationRemoved { get; init; }

    // Step 3: document metadata (PdfMetadataExtraction.Parse)
    public string? ParsedTitle              { get; init; }
    public string? ParsedVersion            { get; init; }
    public string? ParsedPublicationDateRaw { get; init; }

    // Overall outcome
    public int PageCount      { get; init; }
    public int PageErrorCount { get; init; }
    public int WarningCount   { get; init; }
}
