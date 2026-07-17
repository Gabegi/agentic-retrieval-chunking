namespace ProtocolsIndexer.Models;

// One PDF file's complete extraction outcome - everything every step of the pipeline
// (PdfDocumentValidator, PdfNativeMetadataExtractor, PDFDocumentAnalyzer) produced for
// this file, combined into one object by DocumentIntelligenceExtractor. Error is set (and
// every data field null) when the file couldn't be parsed at all (corrupt PDF, backend
// exception) — the orchestrator folds this into the same ExtractionResult<T>.Errors
// bucket CSV's row-level parse errors land in.
public record PDFExtractionResult(
    // True on success, false on failure - every field below except BlobName/FileSizeBytes/
    // Error is null when Ok is false. Technically derivable from Error being null/non-null
    // (every construction site keeps the two in lockstep), but kept explicit so callers can
    // write `if (!result.Ok)` rather than `if (result.Error != null)`, matching the same
    // Ok/Error convention AnalyzeOutcome and PDFStructureExtractorResult already use.
    bool   Ok,
    string BlobName,

    // Step 1: PdfDocumentValidator - free facts, always known once the file has been read
    // off blob storage, independent of whether it goes on to open/analyze successfully.
    long    FileSizeBytes,
    double? PdfSpecVersion,      // e.g. 1.7 - PdfPig's opened.Version; null unless the file fully passed validation

    // Step 2: PdfNativeMetadataExtractor - PdfPig-native, independent of DI. Null only
    // when the file failed before/during opening.
    DocMetadata? NativeMetadata,

    // Step 3: PDFDocumentAnalyzer - the paid Document Intelligence call.
    string?                       RawContent,       // analysis.Content, unsplit, before per-page assembly
    IReadOnlyList<PdfPageRecord>? Pages,
    PdfDocumentStructure?         Structure,
    decimal?                      EstimatedCostUsd,

    ExtractionError? Error)
{
    // Per-page failures/soft-quality signals that don't fail the whole file (e.g. one
    // unreadable page, a likely-scanned page). Folded into the aggregate ExtractionResult
    // by PdfPipelineValidator, same bucket a file-level Error would land in.
    public IReadOnlyList<ExtractionError>   PageErrors  { get; init; } = [];
    public IReadOnlyList<ExtractionWarning> Warnings    { get; init; } = [];

    // Per-step diagnostic snapshot (see PdfExtractionDiagnostics) - currently always null;
    // nothing populates it since the PdfPig backend (the only producer) was removed.
    public PdfExtractionDiagnostics? Diagnostics { get; init; }

    // Page number -> breadcrumb text (e.g. "Section: Chapter 3 > 3.2 Dosage"), built from
    // NativeMetadata.Bookmarks by PDFSectionBreadCrumbBuilder. Empty when the PDF has no
    // outline. Not consumed by anything yet - a future chunk-builder attaches the entry
    // for whichever page(s) a chunk falls on.
    public IReadOnlyDictionary<int, string> SectionBreadcrumbs { get; init; } = new Dictionary<int, string>();
}
