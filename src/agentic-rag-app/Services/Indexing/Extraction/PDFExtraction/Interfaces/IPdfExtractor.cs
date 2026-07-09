using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// Any PDF extraction backend (PdfPig, Document Intelligence, …) implements this.
// Unlike ICsvExtractor's two file-shaped calls, a PDF has one file per document, so a
// single Extract call must return both pages and this file's own parsed metadata —
// re-parsing the same PDF twice per file would double the backend's cost for nothing.
public interface IPdfExtractor
{
    string Name { get; } // "PdfPig" | "DocumentIntelligence" — used by the comparison runner

    PdfFileExtraction Extract(string blobName, byte[] pdfBytes);
}

// One PDF file's extraction outcome. Error is set (and Pages/Index empty) when the
// file couldn't be parsed at all (corrupt PDF, backend exception) — the orchestrator
// folds this into the same ExtractionResult<T>.Errors bucket CSV's row-level parse
// errors land in.
public record PdfFileExtraction(
    IReadOnlyList<PdfPageRecord> Pages,
    PdfIndexRecord?              Index,
    ExtractionError?             Error);
