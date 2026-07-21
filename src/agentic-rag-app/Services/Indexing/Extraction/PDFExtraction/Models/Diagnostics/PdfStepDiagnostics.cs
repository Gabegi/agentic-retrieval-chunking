namespace AgenticRag.Models;

// One pipeline step's non-fatal findings for one PDF file (PdfDocumentValidator,
// PdfNativeMetadataExtractor, PdfDocumentAnalyzer, PdfSectionBreadCrumbBuilder each
// produce one of these) - lets a report answer "which step found this" instead of
// everything landing in one undifferentiated pile.
//
// Report/diagnostic material only - NOT a second source of truth for validation
// gating. PdfPipelineValidator gates on PDFExtractionResult.Ok/Error (the file-level
// outcome) and PageErrors (per-page failures within an otherwise-successful file);
// it must never also fold these per-step Errors into CollectIssues/the error-rate
// gate, or a mirrored hard failure (see ValidationDiagnostics) would be counted
// twice. These are written by WriteReportsAsync for humans to read, nothing else.
public sealed record PdfStepDiagnostics(
    IReadOnlyList<ExtractionWarning> Warnings,
    IReadOnlyList<ExtractionError>   Errors)
{
    public static readonly PdfStepDiagnostics Empty = new([], []);
}
