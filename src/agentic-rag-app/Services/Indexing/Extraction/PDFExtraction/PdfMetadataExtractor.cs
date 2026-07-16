using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Outline;

namespace ProtocolsIndexer.Models
{
    public sealed record Bookmark(string Title, int Level, int? PageNumber);
}

namespace ProtocolsIndexer.Services
{
    // Two unrelated metadata sources, kept in one file:
    // - Parse: Zenya's Title/Version/PublicationDate from blob name + first-page text.
    // - ParseNativeMetadata: the PDF's own Info-dictionary + bookmark tree, via PdfPig.
    internal static class PdfMetadataExtractor
    {
        // Reads pdf native Title/Author/CreationDate + the bookmark tree off an open pdf.
        // - Takes ownership of pdf's lifetime (disposes it here, not in the caller).
        // - Called once, by DocumentIntelligenceExtractor, after preflight opens pdf.
        public static DocMetadata ExtractPdfNativeMetadata(PdfDocument pdf, string blobName, ILogger logger)
        {
            using (pdf)
            {
                var info = pdf.Information;
                var bookmarks = TryGetBookmarks(pdf, blobName, logger);

                var metadata = new DocMetadata(
                    Title:     string.IsNullOrWhiteSpace(info.Title)  ? null : info.Title,
                    Author:    string.IsNullOrWhiteSpace(info.Author) ? null : info.Author,
                    CreatedAt: TryParsePdfDate(info.CreationDate),
                    PageCount: pdf.NumberOfPages,
                    Bookmarks: bookmarks);

                logger.LogDebug(
                    "PdfDocumentValidator: '{Blob}' — {Pages} page(s), title={Title}, author={Author}, created={Created}",
                    blobName, metadata.PageCount, metadata.Title, metadata.Author, metadata.CreatedAt);

                return metadata;
            }
        }

        // Bookmarks/outline tree - PdfPig only, best-effort.
        // - TryGetBookmarks can still throw on a malformed node despite the name; caught here.
        // - null = read failed (skip bookmarks). Empty list = read fine, PDF has none.
        private static IReadOnlyList<Bookmark>? TryGetBookmarks(PdfDocument pdf, string blobName, ILogger logger)
        {
            try
            {
                if (!pdf.TryGetBookmarks(out var bookmarks))
                {
                    logger.LogInformation("No bookmarks/outline found in '{Blob}'.", blobName);
                    return Array.Empty<Bookmark>();
                }

                return bookmarks.GetNodes()
                    .Select(node => new Bookmark(node.Title, node.Level, TryGetPageNumber(node)))
                    .ToList();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Bookmark extraction failed for '{Blob}'; continuing without bookmarks.", blobName);
                return null;
            }
        }

        // PDF dates look like D:20240115093000+01'00' — parse defensively, never throw.
        private static DateTimeOffset? TryParsePdfDate(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            var s = raw.StartsWith("D:") ? raw[2..] : raw;
            return DateTimeOffset.TryParseExact(s[..Math.Min(14, s.Length)], "yyyyMMddHHmmss",
                null, System.Globalization.DateTimeStyles.AssumeUniversal, out var dt) ? dt : null;
        }

        // Page number for a bookmark node.
        // - ExternalBookmarkNode inherits DocumentBookmarkNode but points at another file -
        //   excluded explicitly, its PageNumber isn't a page in this document.
        // - PdfPig's page-number-defaults-to-0 fix (#736/#930) isn't in the pinned 0.1.9,
        //   so an unresolvable destination may be missing from the tree rather than 0.
        private static int? TryGetPageNumber(BookmarkNode node) =>
            node is ExternalBookmarkNode
                ? null
                : node is DocumentBookmarkNode doc && doc.PageNumber > 0
                    ? doc.PageNumber
                    : null;

        // Derives Zenya's Title/Version/PublicationDate for a PDF (no external index
        // file to join against, unlike Zenya's index.csv). Shared by both IPdfExtractor
        // backends so metadata parses identically either way.
        // - Title: prefers nativeTitle (the PDF's own Info-dictionary Title, from
        //   ParseNativeMetadata) when the file actually has one set - real PDF metadata,
        //   not a guess. Falls back to the blob-name-derived title otherwise.
        // - Version/PublicationDateRaw: left empty - no confirmed Cordaan pattern yet,
        //   and unlike Title there's no native PDF field to fall back to.
        // - Previously matched Dutch/LCI-specific regexes on first-page text (ported
        //   from a different corpus); removed after confirming they don't apply to
        //   Cordaan's documents, along with the first-page-text parameter they read.
        public static PdfIndexRecord Parse(string blobName, string? nativeTitle = null)
        {
            var title = !string.IsNullOrWhiteSpace(nativeTitle)
                ? nativeTitle
                : blobName.Split('/')[0]
                    .Replace(".pdf", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("-", " ");

            return new PdfIndexRecord
            {
                BlobName           = blobName,
                Title              = title,
                Version            = "",
                PublicationDateRaw = "",
            };
        }
    }
}
