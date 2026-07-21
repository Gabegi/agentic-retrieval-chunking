namespace AgenticRagApp.Models;

// Everything read off a PdfDocument via PdfPig, independent of Document Intelligence:
// the native Info-dictionary (Title/Author/CreationDate) plus the outline/bookmark tree
// (Bookmarks), both read by PdfNativeMetadataExtractor.ExtractPdfNativeMetadata.
public record DocMetadata(string? Title, string? Author, DateTimeOffset? CreatedAt, int PageCount, IReadOnlyList<Bookmark>? Bookmarks);
