using System.Diagnostics.CodeAnalysis;
using ProtocolsIndexer.Models;
using UglyToad.PdfPig;

namespace ProtocolsIndexer.Services;

// Document Intelligence's own hard limits, checked locally/for free before spending a
// paid analyze call. Both are fixed at the service level regardless of pricing tier -
// see https://learn.microsoft.com/azure/ai-services/document-intelligence/service-limits
// ("Adjustable: No" for both max document size and max pages, even on Standard S0).
// Split into a pre-open size check (IsPDFSizeOkForDI) and a post-open page-count check
// (IsPDFPageCountOkForDI, both zero and too-many-pages) so the caller
// (DocumentIntelligenceExtractor.IsPDFValid) can reject oversized files before ever
// opening them with PdfDocumentOpener. Knows nothing about metadata - that's
// PdfMetadataExtraction's job entirely.
public static class PdfPreFlight
{
    public const long MaxBytes = 500L * 1024 * 1024; // DI hard limit, all paid tiers
    public const int  MaxPages = 2000;                // DI hard limit per analyze call, all paid tiers

    public static bool IsPDFSizeOkForDI(byte[] pdfBytes, string blobName, [NotNullWhen(false)] out ExtractionError? error)
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

    public static bool IsPDFPageCountOkForDI(PdfDocument pdf, string blobName, [NotNullWhen(false)] out ExtractionError? error)
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
