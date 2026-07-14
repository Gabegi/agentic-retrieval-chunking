using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;

namespace ProtocolsIndexer.Services;

// Step 1 of PdfPigExtractor: open the raw bytes & structurally validate the document.
// Exception types caught here are PdfPig's own (confirmed via reflection against the
// referenced 0.1.9 build, not just docs) - anything else falls through to the generic
// catch and is reported as Unknown rather than mislabeled as a specific cause.
internal sealed class PdfDocumentOpener
{
    private readonly ILogger _logger;

    public PdfDocumentOpener(ILogger logger)
    {
        _logger = logger;
    }

    public bool TryOpenAndValidate(
        byte[] pdfBytes, string blobName,
        [NotNullWhen(true)]  out PdfDocument?    pdf,
        [NotNullWhen(false)] out ExtractionError? error)
    {
        PdfDocument? opened = null;
        try
        {
            // Open document
            //  PdfPig parses the byte stream: reads the PDF header, cross-reference table/trailer, decodes the document catalog and page tree, etc.
            opened = PdfDocument.Open(pdfBytes);

            // checks # of pages
            if (opened.NumberOfPages == 0)
            {
                opened.Dispose();
                pdf   = null;
                error = OpenError(blobName, PdfOpenFailureReason.EmptyDocument, "PDF contains zero pages.");
                return false;
            }

            _logger.LogInformation(
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
            _logger.LogWarning(ex, "PDF '{Blob}' is encrypted and could not be opened.", blobName);
            return false;
        }
        // Structurally broken PDF: corrupt header, broken xref/trailer, malformed
        // object streams, truncated file, etc.
        catch (PdfDocumentFormatException ex)
        {
            opened?.Dispose();
            pdf   = null;
            error = OpenError(blobName, PdfOpenFailureReason.MalformedFormat, $"PDF structure is malformed: {ex.Message}");
            _logger.LogWarning(ex, "PDF '{Blob}' has a malformed/corrupt structure.", blobName);
            return false;
        }
        // Anything else PdfPig (or the runtime) threw while opening/inspecting the
        // document - not confidently attributable to a specific cause above.
        catch (Exception ex)
        {
            opened?.Dispose();
            pdf   = null;
            error = OpenError(blobName, PdfOpenFailureReason.Unknown, $"Not a parseable PDF: {ex.Message}");
            _logger.LogWarning(ex, "PDF '{Blob}' failed to open for an unrecognized reason.", blobName);
            return false;
        }
    }

    private static ExtractionError OpenError(string blobName, PdfOpenFailureReason reason, string message) =>
        new() { DocumentId = blobName, Message = message, Reason = reason };
}
