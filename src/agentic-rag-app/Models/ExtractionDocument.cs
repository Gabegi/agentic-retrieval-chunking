namespace ProtocolsIndexer.Models;

// Extractor-agnostic document handed to the chunking and indexing pipeline.
// SourceId is the chunking boundary — the chunker never blends chunks across different SourceIds.
public record ExtractionDocument(
    string SourceId,    // grouping/chunking boundary — DocumentId (CSV) or blobName (PDF)
    int    Ordinal,     // page number (PDF) or row index (CSV) — used for ordering only
    string Content,
    IReadOnlyDictionary<string, string> Metadata
);
