namespace ProtocolsIndexer.Models;

// Structured category for a file-level PDF open/parse failure, set by
// PdfPigExtractor.TryOpenAndValidate. Lets PdfValidationReport break down
// "how many files failed" by cause instead of grepping free-text messages.
public enum PdfOpenFailureReason
{
    Unknown,          // unexpected exception PdfPig doesn't have a dedicated type for
    Encrypted,        // PdfDocumentEncryptedException - password-protected/unsupported encryption
    MalformedFormat,  // PdfDocumentFormatException - corrupt header, broken xref, malformed objects
    EmptyDocument,    // opened fine but has zero pages
    NoReadablePages,  // opened fine but every page failed extraction
}
