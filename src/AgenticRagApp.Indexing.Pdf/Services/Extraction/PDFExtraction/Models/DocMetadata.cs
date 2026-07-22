namespace AgenticRagApp.Indexing.Pdf.Models;

// Everything read off a PdfDocument via PdfPig, independent of Document Intelligence:
// the native Info-dictionary (Title/Author/CreationDate/ModDate/Producer/Creator/Subject/
// Keywords) plus the outline/bookmark tree (Bookmarks), both read by
// PdfNativeMetadataExtractor.ExtractPdfNativeMetadata.
//
// Producer/Creator/Subject/Keywords are diagnostics-only today (see
// PdfNativeMetadataExtractor's Producer-missing warning) - not carried into
// ExtractionDocument/DocumentChunk, same as FileSizeBytes/PdfSpecVersion aren't (see
// ExtractionDocument's own comment on that).
public record DocMetadata(
    string? Title, string? Author, DateTimeOffset? CreatedAt, DateTimeOffset? ModDate,
    string? Producer, string? Creator, string? Subject, string? Keywords,
    int PageCount, IReadOnlyList<Bookmark>? Bookmarks);
