using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;

namespace ProtocolsIndexer.Services;

// Local, free structural gates run before/around opening a PDF, ahead of any paid
// Document Intelligence call. IsPDFValid is the entry point: pre-open size check
// (too-large/empty), then open/validate (encrypted/corrupt/malformed), then post-open
// page count (zero/too-many-pages) - in that order, cheapest first, so a too-large file
// never gets opened and an unopenable file never gets page-counted. TryOpenAndValidate
// is also exposed on its own for PdfPigExtractor, which needs the open/validate step but
// not Document Intelligence's size/page limits. The size/page-count limits are Document
// Intelligence's own hard limits, fixed at the service level regardless of pricing tier -
// see https://learn.microsoft.com/azure/ai-services/document-intelligence/service-limits
// ("Adjustable: No" for both max document size and max pages, even on Standard S0).
// Knows nothing about metadata - that's PdfMetadataExtraction's job entirely.
public static class PdfDocumentValidator
{
    public const long MaxBytes = 500L * 1024 * 1024; // DI hard limit, all paid tiers
    public const int  MaxPages = 2000;                // DI hard limit per analyze call, all paid tiers

    // All three checks Document Intelligence needs before a paid analyze call, in the
    // right order. Returns the opened, page-count-validated PdfDocument on success so the
    // caller can read metadata/bookmarks from it - the caller owns disposing it from
    // there; on failure (including a page-count rejection after a successful open) this
    // disposes it internally, since pdf is null and the caller never gets a handle to it.
    public static bool IsPDFValid(
        byte[] pdfBytes, string blobName, ILogger logger,
        [NotNullWhen(true)]  out PdfDocument?    pdf,
        [NotNullWhen(false)] out ExtractionError? error)
    {
        pdf = null;

        if (!IsPDFSizeOkForDI(pdfBytes, blobName, out error))
            return false;

        if (!TryOpenAndValidate(pdfBytes, blobName, logger, out pdf, out error))
            return false;

        if (!IsPDFPageCountOkForDI(pdf, blobName, out error))
        {
            pdf.Dispose();
            pdf = null;
            return false;
        }

        return true;
    }

    // Opens the raw bytes with PdfPig and structurally validates the document. Exception
    // types caught here are PdfPig's own (confirmed via reflection against the referenced
    // 0.1.9 build, not just docs) - anything else falls through to the generic catch and
    // is reported as Unknown rather than mislabeled as a specific cause.
    public static bool TryOpenAndValidate(
        byte[] pdfBytes, string blobName, ILogger logger,
        [NotNullWhen(true)]  out PdfDocument?    pdf,
        [NotNullWhen(false)] out ExtractionError? error)
    {
        PdfDocument? opened = null;
        try
        {
            // Open document
            //  PdfPig parses the byte stream: reads the PDF header, cross-reference table/trailer, decodes the document catalog and page tree, etc.
            opened = PdfDocument.Open(pdfBytes);

            logger.LogInformation(
                "Opened PDF '{Blob}': {Pages} page(s), version {Version}",
                blobName, opened.NumberOfPages, opened.Version);

            pdf   = opened;
            error = null;
            return true;
        }
        // Password-protected / unsupported-encryption PDFs. PdfPig can't recover
        // content here at all - the caller needs the actual password, so this is
        // worth telling apart from a plain corrupt file.
        catch (PdfDocumentEncryptedException ex)
        {
            opened?.Dispose();
            pdf   = null;
            error = OpenError(blobName, PdfOpenFailureReason.Encrypted, $"PDF is encrypted: {ex.Message}");
            logger.LogWarning(ex, "PDF '{Blob}' is encrypted and could not be opened.", blobName);
            return false;
        }
        // Structurally broken PDF: corrupt header, broken xref/trailer, malformed
        // object streams, truncated file, etc.
        catch (PdfDocumentFormatException ex)
        {
            opened?.Dispose();
            pdf   = null;
            error = OpenError(blobName, PdfOpenFailureReason.MalformedFormat, $"PDF structure is malformed: {ex.Message}");
            logger.LogWarning(ex, "PDF '{Blob}' has a malformed/corrupt structure.", blobName);
            return false;
        }
        // Anything else PdfPig (or the runtime) threw while opening/inspecting the
        // document - not confidently attributable to a specific cause above.
        catch (Exception ex)
        {
            opened?.Dispose();
            pdf   = null;
            error = OpenError(blobName, PdfOpenFailureReason.Unknown, $"Not a parseable PDF: {ex.Message}");
            logger.LogWarning(ex, "PDF '{Blob}' failed to open for an unrecognized reason.", blobName);
            return false;
        }
    }

    private static bool IsPDFSizeOkForDI(byte[] pdfBytes, string blobName, [NotNullWhen(false)] out ExtractionError? error)
    {
        if (pdfBytes.Length == 0)
        {
            error = new ExtractionError { DocumentId = blobName, Message = "Empty file (0 bytes).", Reason = PdfOpenFailureReason.EmptyFile };
            return false;
        }

        if (pdfBytes.Length > MaxBytes)
        {
            error = new ExtractionError
            {
                DocumentId = blobName,
                Message    = $"File is {pdfBytes.Length / 1024.0 / 1024.0:F1} MB, exceeds the {MaxBytes / 1024 / 1024} MB Document Intelligence limit.",
                Reason     = PdfOpenFailureReason.TooLarge,
            };
            return false;
        }

        error = null;
        return true;
    }

    private static ExtractionError OpenError(string blobName, PdfOpenFailureReason reason, string message) =>
        new() { DocumentId = blobName, Message = message, Reason = reason };

    private static bool IsPDFPageCountOkForDI(PdfDocument pdf, string blobName, [NotNullWhen(false)] out ExtractionError? error)
    {
        if (pdf.NumberOfPages == 0)
        {
            error = new ExtractionError { DocumentId = blobName, Message = "PDF contains zero pages.", Reason = PdfOpenFailureReason.EmptyDocument };
            return false;
        }

        if (pdf.NumberOfPages > MaxPages)
        {
            error = new ExtractionError
            {
                DocumentId = blobName,
                Message    = $"{pdf.NumberOfPages} pages exceeds the {MaxPages}-page Document Intelligence limit per analyze call; split before submitting.",
                Reason     = PdfOpenFailureReason.TooManyPages,
            };
            return false;
        }

        error = null;
        return true;
    }
}
