using Microsoft.Extensions.Logging;
using AgenticRagApp.Indexing.Pdf.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Outline;

namespace AgenticRagApp.Indexing.Pdf.Services
{
    // The PDF's own Info-dictionary + bookmark tree, read via PdfPig.
    internal static class PdfNativeMetadataExtractor
    {
        // Reads pdf native Title/Author/CreationDate/ModDate/Producer/Creator/Subject/
        // Keywords + the bookmark tree off an open pdf.
        // - Takes ownership of pdf's lifetime (disposes it here, not in the caller).
        // - Called once, by DocumentIntelligenceExtractor, after preflight opens pdf.
        // diagnostics is report/diagnostic material only (see PdfStepDiagnostics) - never
        // fails the file, since native metadata is a nice-to-have, not required for DI to
        // process the document.
        public static DocMetadata ExtractPdfNativeMetadata(
            PdfDocument pdf, string blobName, ILogger logger, out PdfStepDiagnostics diagnostics)
        {
            using (pdf)
            {
                var warnings = new List<ExtractionWarning>();

                var info = pdf.Information;
                var bookmarks = TryGetBookmarks(pdf, blobName, logger, warnings);

                var title     = string.IsNullOrWhiteSpace(info.Title)     ? null : info.Title;
                var author    = string.IsNullOrWhiteSpace(info.Author)    ? null : info.Author;
                var producer  = string.IsNullOrWhiteSpace(info.Producer)  ? null : info.Producer;
                var creator   = string.IsNullOrWhiteSpace(info.Creator)   ? null : info.Creator;
                var subject   = string.IsNullOrWhiteSpace(info.Subject)   ? null : info.Subject;
                var keywords  = string.IsNullOrWhiteSpace(info.Keywords)  ? null : info.Keywords;
                var createdAt = TryParsePdfDate(info.CreationDate, blobName, warnings, "CreationDate");
                var modDate   = TryParsePdfDate(info.ModifiedDate, blobName, warnings, "ModDate");

                if (title is null)
                    warnings.Add(new ExtractionWarning
                    {
                        DocumentId = blobName,
                        Message    = "No native Title in the PDF's Info dictionary - falls back to a filename-derived title downstream.",
                    });

                if (author is null)
                    warnings.Add(new ExtractionWarning { DocumentId = blobName, Message = "No native Author in the PDF's Info dictionary." });

                if (producer is null)
                    warnings.Add(new ExtractionWarning
                    {
                        DocumentId = blobName,
                        Message    = "No native Producer in the PDF's Info dictionary — possible non-standard export pipeline.",
                    });

                if (bookmarks is { Count: > 0 })
                    warnings.Add(new ExtractionWarning
                    {
                        DocumentId = blobName,
                        Message    = $"{bookmarks.Count} bookmark(s) found, max outline depth {bookmarks.Max(b => b.Level) + 1}.",
                    });

                var metadata = new DocMetadata(
                    Title:     title,
                    Author:    author,
                    CreatedAt: createdAt,
                    ModDate:   modDate,
                    Producer:  producer,
                    Creator:   creator,
                    Subject:   subject,
                    Keywords:  keywords,
                    PageCount: pdf.NumberOfPages,
                    Bookmarks: bookmarks);

                logger.LogDebug(
                    "PdfNativeMetadataExtractor: '{Blob}' — {Pages} page(s), title={Title}, author={Author}, created={Created}, modified={Modified}, producer={Producer}",
                    blobName, metadata.PageCount, metadata.Title, metadata.Author, metadata.CreatedAt, metadata.ModDate, metadata.Producer);

                diagnostics = new PdfStepDiagnostics(warnings, []);
                return metadata;
            }
        }

        // Bookmarks/outline tree - PdfPig only, best-effort.
        // - TryGetBookmarks can still throw on a malformed node despite the name; caught here.
        // - null = read failed (skip bookmarks). Empty list = read fine, PDF has none.
        private static IReadOnlyList<Bookmark>? TryGetBookmarks(
            PdfDocument pdf, string blobName, ILogger logger, List<ExtractionWarning> warnings)
        {
            try
            {
                if (!pdf.TryGetBookmarks(out var bookmarks))
                {
                    logger.LogInformation("No bookmarks/outline found in '{Blob}'.", blobName);
                    warnings.Add(new ExtractionWarning { DocumentId = blobName, Message = "No bookmarks/outline found." });
                    return Array.Empty<Bookmark>();
                }

                return bookmarks.GetNodes()
                    .Select(node => new Bookmark(node.Title, node.Level, TryGetPageNumber(node), node is ExternalBookmarkNode))
                    .ToList();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Bookmark extraction failed for '{Blob}'; continuing without bookmarks.", blobName);
                warnings.Add(new ExtractionWarning { DocumentId = blobName, Message = $"Bookmark extraction failed: {ex.Message}" });
                return null;
            }
        }

        // PDF dates look like D:20240115093000+01'00' — parse defensively, never throw.
        // Only the first 14 chars (yyyyMMddHHmmss) are read; the trailing timezone offset
        // (+01'00' above) is discarded and the result is always treated as UTC
        // (AssumeUniversal) - fine for CreatedAt as a display value, but don't use it for
        // precise time-of-day diffing, since it can be off by the source PDF's real offset.
        private static DateTimeOffset? TryParsePdfDate(string? raw, string blobName, List<ExtractionWarning> warnings, string fieldName)
        {
            if (string.IsNullOrEmpty(raw))
            {
                warnings.Add(new ExtractionWarning { DocumentId = blobName, Message = $"No native {fieldName} in the PDF's Info dictionary." });
                return null;
            }

            var s = raw.StartsWith("D:") ? raw[2..] : raw;
            if (!DateTimeOffset.TryParseExact(s[..Math.Min(14, s.Length)], "yyyyMMddHHmmss",
                    null, System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
            {
                warnings.Add(new ExtractionWarning { DocumentId = blobName, Message = $"{fieldName} '{raw}' could not be parsed." });
                return null;
            }

            if (dt > DateTimeOffset.UtcNow)
                warnings.Add(new ExtractionWarning { DocumentId = blobName, Message = $"{fieldName} '{dt:O}' is in the future." });

            return dt;
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
    }
}
