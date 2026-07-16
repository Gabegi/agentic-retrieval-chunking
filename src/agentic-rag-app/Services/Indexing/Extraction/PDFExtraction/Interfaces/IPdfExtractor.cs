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

    Task<PdfFileExtraction> ExtractPDFAsync(string blobName, byte[] pdfBytes, CancellationToken ct = default);
}

// One PDF file's extraction outcome. Error is set (and Pages empty) when the file
// couldn't be parsed at all (corrupt PDF, backend exception) — the orchestrator folds
// this into the same ExtractionResult<T>.Errors bucket CSV's row-level parse errors
// land in.
public record PdfFileExtraction(
    IReadOnlyList<PdfPageRecord> Pages,
    ExtractionError?             Error,
    decimal?                     EstimatedCostUsd = null) // set by paid backends (e.g. Document Intelligence) for the comparison report
{
    // Per-page failures/soft-quality signals that don't fail the whole file (e.g. one
    // unreadable page, a likely-scanned page). Folded into the aggregate ExtractionResult
    // by PdfExtractionAggregation, same bucket a file-level Error would land in.
    public IReadOnlyList<ExtractionError>   PageErrors  { get; init; } = [];
    public IReadOnlyList<ExtractionWarning> Warnings    { get; init; } = [];

    // Per-step diagnostic snapshot (see PdfExtractionDiagnostics) - currently always null;
    // nothing populates it since the PdfPig backend (the only producer) was removed.
    public PdfExtractionDiagnostics? Diagnostics { get; init; }

    // Native PDF Info-dictionary metadata (see DocMetadata), read via PdfPig during
    // DocumentIntelligenceExtractor's preflight open. Null only when the file failed
    // before opening.
    public DocMetadata? NativeMetadata { get; init; }

    // Raw structural ingredients (headings/tables/bookmarks/page dimensions/selection
    // marks, each carrying an Offset) for whatever builds ChunkMetadata once chunk
    // boundaries exist downstream - see PDFStructureExtractor.PdfStructureMetadata.
    public PdfStructureMetadata? StructureMetadata { get; init; }
}
