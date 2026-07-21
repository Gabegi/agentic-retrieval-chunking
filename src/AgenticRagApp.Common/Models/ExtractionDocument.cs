namespace IndexingShared.Models;

// Extractor-agnostic document handed to the chunking and indexing pipeline.
// SourceId is the chunking boundary — the chunker never blends chunks across different SourceIds.
// Metadata uses Dictionary (not IReadOnlyDictionary) so Durable/STJ can deserialize it reliably.
public record ExtractionDocument(
    string SourceId,                        // grouping/chunking boundary — DocumentId (CSV) or blobName (PDF)
    int    Ordinal,                         // page number (PDF) or row index (CSV) — used for ordering only
    string Content,
    Dictionary<string, string> Metadata
);
