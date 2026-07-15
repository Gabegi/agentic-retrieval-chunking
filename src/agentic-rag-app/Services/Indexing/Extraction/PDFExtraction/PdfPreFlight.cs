using System.Diagnostics.CodeAnalysis;
using ProtocolsIndexer.Models;
using UglyToad.PdfPig;

namespace ProtocolsIndexer.Services;

// Document Intelligence's own hard limits, checked locally/for free before spending a
// paid analyze call. Both are fixed at the service level regardless of pricing tier -
// see https://learn.microsoft.com/azure/ai-services/document-intelligence/service-limits
// ("Adjustable: No" for both max document size and max pages, even on Standard S0).
// Split into a pre-open size check (IsPDFSizeOkForDI) and a post-open check
// (IsPDFMaxPageOkForDI) so the caller (DocumentIntelligenceExtractor.IsPDFValid) can
// reject oversized files before ever opening them with PdfDocumentOpener, and never
// opens a document twice.
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

    public static bool IsPDFMaxPageOkForDI(
        PdfDocument pdf, string blobName,
        [NotNullWhen(true)]  out DocMetadata?    meta,
        [NotNullWhen(false)] out ExtractionError? error)
    {
        if (pdf.NumberOfPages > MaxPages)
        {
            meta  = null;
            error = new ExtractionError
            {
                DocumentId = blobName,
                Message    = $"{pdf.NumberOfPages} pages exceeds the {MaxPages}-page Document Intelligence limit per analyze call; split before submitting.",
                Reason     = PdfOpenFailureReason.TooManyPages,
            };
            return false;
        }

        var info = pdf.Information;
        meta = new DocMetadata(
            Title:     string.IsNullOrWhiteSpace(info.Title)  ? null : info.Title,
            Author:    string.IsNullOrWhiteSpace(info.Author) ? null : info.Author,
            CreatedAt: TryParsePdfDate(info.CreationDate),
            PageCount: pdf.NumberOfPages);
        error = null;
        return true;
    }

    private static DateTimeOffset? TryParsePdfDate(string? raw)
    {
        // PDF dates look like D:20240115093000+01'00' — parse defensively, never throw.
        if (string.IsNullOrEmpty(raw)) return null;
        var s = raw.StartsWith("D:") ? raw[2..] : raw;
        return DateTimeOffset.TryParseExact(s[..Math.Min(14, s.Length)], "yyyyMMddHHmmss",
            null, System.Globalization.DateTimeStyles.AssumeUniversal, out var dt) ? dt : null;
    }
}
