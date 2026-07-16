using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// A PDF extraction backend (Document Intelligence) implements this. Unlike
// ICsvExtractor's two file-shaped calls, a PDF has one file per document, so a single
// Extract call must return both pages and this file's own parsed metadata —
// re-parsing the same PDF twice per file would double the backend's cost for nothing.
// Async because DocumentIntelligenceExtractor does real network I/O (the analyze call,
// plus its retry backoff).
public interface IPdfExtractor
{
    string Name { get; } // "DocumentIntelligence"

    Task<PDFExtractionResult> ExtractPDFAsync(string blobName, byte[] pdfBytes, CancellationToken ct = default);
}

// One PDF file's complete extraction outcome - everything every step of the pipeline
// (PdfDocumentValidator, PdfNativeMetadataExtractor, PDFStructureExtractor) produced for
// this file, combined into one object by DocumentIntelligenceExtractor. Error is set (and
// every data field null) when the file couldn't be parsed at all (corrupt PDF, backend
// exception) — the orchestrator folds this into the same ExtractionResult<T>.Errors
// bucket CSV's row-level parse errors land in.
public record PDFExtractionResult(
    bool   Ok,
    string BlobName,

    // Step 1: PdfDocumentValidator - free facts, always known once the file has been read
    // off blob storage, independent of whether it goes on to open/analyze successfully.
    long    FileSizeBytes,
    double? PdfSpecVersion,      // e.g. 1.7 - PdfPig's opened.Version; null unless the file fully passed validation

    // Step 2: PdfNativeMetadataExtractor - PdfPig-native, independent of DI. Null only
    // when the file failed before/during opening.
    DocMetadata? NativeMetadata,

    // Step 3: PDFStructureExtractor - the paid Document Intelligence call.
    string?                       RawContent,       // analysis.Content, unsplit, before per-page assembly
    IReadOnlyList<PdfPageRecord>? Pages,
    PdfDocumentStructure?         Structure,
    decimal?                      EstimatedCostUsd,

    ExtractionError? Error)
{
    // Per-page failures/soft-quality signals that don't fail the whole file (e.g. one
    // unreadable page, a likely-scanned page). Folded into the aggregate ExtractionResult
    // by PdfExtractionAggregation, same bucket a file-level Error would land in.
    public IReadOnlyList<ExtractionError>   PageErrors  { get; init; } = [];
    public IReadOnlyList<ExtractionWarning> Warnings    { get; init; } = [];

    // Per-step diagnostic snapshot (see PdfExtractionDiagnostics) - currently always null;
    // nothing populates it since the PdfPig backend (the only producer) was removed.
    public PdfExtractionDiagnostics? Diagnostics { get; init; }
}
