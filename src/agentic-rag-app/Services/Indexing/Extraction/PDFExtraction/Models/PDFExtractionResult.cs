namespace ProtocolsIndexer.Models;

// One PDF file's complete extraction outcome - everything every step of the pipeline
// (PdfDocumentValidator, PdfNativeMetadataExtractor, PDFDocumentAnalyzer) produced for
// this file, combined into one object by DocumentIntelligenceExtractor. Error is set (and
// every data field null) when the file couldn't be parsed at all (corrupt PDF, backend
// exception) — the orchestrator folds this into the same ExtractionResult<T>.Errors
// bucket CSV's row-level parse errors land in.
//
// Explicit properties + constructor (not positional record syntax) so the Ok/Error
// invariant can be enforced at construction: Ok and Error can't diverge, so callers of
// either f.Ok or f.Error != null are provably checking the same thing, rather than
// relying on every construction site happening to keep them in lockstep by convention.
public record PDFExtractionResult
{
    // True on success, false on failure - every field below except BlobName/FileSizeBytes/
    // Error is null when Ok is false.
    public bool   Ok       { get; }
    public string BlobName { get; }

    // Step 1: PdfDocumentValidator - free facts, always known once the file has been read
    // off blob storage, independent of whether it goes on to open/analyze successfully.
    public long    FileSizeBytes  { get; }
    public double? PdfSpecVersion { get; } // e.g. 1.7 - PdfPig's opened.Version; null unless the file fully passed validation

    // Step 2: PdfNativeMetadataExtractor - PdfPig-native, independent of DI. Null only
    // when the file failed before/during opening.
    public DocMetadata? NativeMetadata { get; }

    // Step 3: PDFDocumentAnalyzer - the paid Document Intelligence call.
    public string? RawContent { get; } // analysis.Content, unsplit, before per-page assembly

    // Raw extractor output - mojibake, blank-line noise, etc. not yet repaired. Never
    // consume this for content; go through PdfCleanResult (PdfPipelineValidator.Aggregate
    // -> PdfCleaner.Clean) instead, so the extract-vs-clean reconciliation check stays a
    // real comparison of two independent states, not the same data read twice.
    public IReadOnlyList<PdfPageRecord>? Pages { get; }
    public PdfDocumentStructure?         Structure { get; }
    public decimal?                      EstimatedCostUsd { get; }

    public ExtractionError? Error { get; }

    public PDFExtractionResult(
        bool ok, string blobName, long fileSizeBytes, double? pdfSpecVersion,
        DocMetadata? nativeMetadata, string? rawContent, IReadOnlyList<PdfPageRecord>? pages,
        PdfDocumentStructure? structure, decimal? estimatedCostUsd, ExtractionError? error)
    {
        if (ok != (error is null))
            throw new ArgumentException(
                $"PDFExtractionResult for '{blobName}': Ok={ok} but Error={(error is null ? "null" : "set")} - they must agree.");
        if (ok && pages is null)
            throw new ArgumentException($"PDFExtractionResult for '{blobName}': Ok=true but Pages is null.");

        Ok               = ok;
        BlobName         = blobName;
        FileSizeBytes    = fileSizeBytes;
        PdfSpecVersion   = pdfSpecVersion;
        NativeMetadata   = nativeMetadata;
        RawContent       = rawContent;
        Pages            = pages;
        Structure        = structure;
        EstimatedCostUsd = estimatedCostUsd;
        Error            = error;
    }

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
