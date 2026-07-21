namespace AgenticRag.Models;

// One PDF page handed to the chunking pipeline. Fully typed on purpose - PDF is this
// project's only source (see docs/plan210726.md's "no generic" note), and a Metadata
// string dictionary is exactly what let six CSV-era fields (folder_path, quick_code,
// check_date, version, summary, plus a Heading nobody ever set) sit unused for so long
// without anyone noticing: nothing forced a reader to account for every key. A typed
// record forces that - every field here is either read by ChunkingService or visibly
// unused right here, not buried behind a string key. See docs/chunking-rewrite-plan.md.
public record ExtractionDocument(
    string SourceId,   // grouping/chunking boundary — the chunker never blends chunks across different SourceIds; blobName for PDF
    int    Ordinal,    // page number — used for ordering only

    string Content,

    // Native PDF Title if the file has one set, else a filename-derived fallback -
    // see PDFDocumentAnalyzer.GetTitle. Same value on every page of one file.
    string Title,

    // The blob's own storage LastModified, full precision (not date-truncated - that
    // truncation only ever mattered for ExtractionService's own diff comparison, which
    // reads the blob property directly, not this field).
    DateTimeOffset? LastModifiedDate,

    // Section context for this page, when the PDF has it - Breadcrumb (from the bookmark
    // outline, hierarchical: "Chapter 3 > 3.2 Dosage") is preferred when present; Heading
    // (Document Intelligence's own title/sectionHeading-role detection) works even without
    // an outline. Null means genuinely nothing detected for this page, not "unknown."
    string? Breadcrumb,
    string? Heading,

    // Native PDF Info-dictionary facts (PdfNativeMetadataExtractor). Same value on every
    // page of one file.
    string?         Author,
    DateTimeOffset? CreatedAt,

    // Document Intelligence structural signals for this page (PdfDocumentStructure).
    // TableCount is a real 0, not "unknown," when DI detected no tables on this page.
    int                   TableCount,
    double?               AverageWordConfidence,
    IReadOnlyList<string> FigureCaptions
);
