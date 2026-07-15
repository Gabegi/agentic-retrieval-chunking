namespace ProtocolsIndexer.Models;

// Native PDF Info-dictionary metadata (Title/Author/CreationDate), read by
// PdfMetadataExtraction.ParseNativeMetadata.
// Distinct from PdfIndexRecord, which is Zenya's own Title/Version parsed from the blob
// name and first-page text — this is often empty or unreliable on scanned medical PDFs,
// so treat it as a secondary signal, not a replacement.
public record DocMetadata(string? Title, string? Author, DateTimeOffset? CreatedAt, int PageCount);
