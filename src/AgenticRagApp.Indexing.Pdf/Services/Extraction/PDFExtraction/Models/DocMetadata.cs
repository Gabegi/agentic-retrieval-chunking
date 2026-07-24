namespace AgenticRagApp.Indexing.Pdf.Models;

// Everything read off a PdfDocument via PdfPig, independent of Document Intelligence:
// the native Info-dictionary (Title/Author/CreationDate/ModDate/Producer/Creator/Subject/
// Keywords), the outline/bookmark tree (Bookmarks), whether the file carries any
// encryption/permission restrictions (IsEncrypted - distinct from PdfDocumentValidator's
// Encrypted failure reason, which is for files that couldn't be opened at all; a file can
// open fine and still report IsEncrypted=true, e.g. owner-password-only permission
// restrictions), its AcroForm fields if any (FormFields), and its XMP metadata packet if
// present (Xmp) - all read by PdfNativeMetadataExtractor.ExtractPdfNativeMetadata.
//
// Producer/Creator/Subject/Keywords are diagnostics-only today (see
// PdfNativeMetadataExtractor's Producer-missing warning) - not carried into
// ExtractionDocument/DocumentChunk, same as FileSizeBytes/PdfSpecVersion aren't (see
// ExtractionDocument's own comment on that).
public record DocMetadata(
    string? Title, string? Author, DateTimeOffset? CreatedAt, DateTimeOffset? ModDate,
    string? Producer, string? Creator, string? Subject, string? Keywords,
    int PageCount, IReadOnlyList<Bookmark>? Bookmarks,
    bool IsEncrypted, IReadOnlyList<AcroFormField>? FormFields,
    // Form-level (not per-field): PdfPig's AcroForm.SignatureFlags has the
    // SignaturesExist bit set when the form contains at least one digital-signature
    // field - null when the PDF has no AcroForm at all (FormFields is also empty in
    // that case), distinct from false (has a form, just no signature fields).
    bool? FormHasSignatureFields,
    XmpFacts? Xmp);
