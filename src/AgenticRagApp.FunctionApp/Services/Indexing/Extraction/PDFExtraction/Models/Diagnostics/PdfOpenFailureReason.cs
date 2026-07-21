namespace AgenticRagApp.Models;

// Structured category for a file-level PDF open/parse failure, set by
// PdfDocumentValidator.TryOpenAndValidate. Lets PdfValidationReport break down
// "how many files failed" by cause instead of grepping free-text messages.
public enum PdfOpenFailureReason
{
    Unknown,          // unexpected exception PdfPig doesn't have a dedicated type for
    Encrypted,        // PdfDocumentEncryptedException - password-protected/unsupported encryption
    MalformedFormat,  // PdfDocumentFormatException - corrupt header, broken xref, malformed objects
    EmptyDocument,    // opened fine but has zero pages
    NoReadablePages,  // opened fine but every page failed extraction
    EmptyFile,        // 0-byte input - never reaches PdfPig (PdfDocumentValidator)
    TooLarge,         // exceeds Document Intelligence's max document size (PdfDocumentValidator)
    TooManyPages,     // exceeds Document Intelligence's max pages per analyze call (PdfDocumentValidator)
    Throttled,        // Document Intelligence returned 429 and retries were exhausted
    DiServiceError,   // Document Intelligence returned a non-429 request failure
    UnexpectedContentFormat, // DI returned Text instead of the requested Markdown - offsets would be untrustworthy
    MissingAnalysisResult,   // AnalyzeOutcome.Ok was true but Result was null - an internal bug, not a DI failure
}
