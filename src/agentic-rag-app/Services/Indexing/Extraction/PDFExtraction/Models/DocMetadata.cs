namespace ProtocolsIndexer.Models;

// Everything read off a PdfDocument via PdfPig, independent of Document Intelligence:
// the native Info-dictionary (Title/Author/CreationDate) plus the outline/bookmark tree
// (Bookmarks), both read by PdfMetadataExtractor.ParseNativeMetadata.
// Distinct from PdfIndexRecord, which is Zenya's own Title/Version parsed from the blob
// name and first-page text — this is often empty or unreliable on scanned medical PDFs,
// so treat it as a secondary signal, not a replacement.
public record DocMetadata(string? Title, string? Author, DateTimeOffset? CreatedAt, int PageCount, IReadOnlyList<Bookmark>? Bookmarks);
