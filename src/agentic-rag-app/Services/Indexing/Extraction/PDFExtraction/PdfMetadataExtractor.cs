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
    // Every way this pipeline derives a PDF's metadata, from two entirely different
    // sources: Parse derives Zenya's own Title/Version/PublicationDate from the blob
    // name and first-page text (no PdfPig involved at all); ParseNativeMetadata reads
    // the PDF's own Info-dictionary and outline/bookmark tree straight off an
    // already-open PdfDocument via PdfPig, independent of Document Intelligence entirely.
    // Kept in one file because both are "metadata about the file," not because they
    // share implementation - they don't call into each other.
    internal static class PdfMetadataExtractor
    {
        // The one call DocumentIntelligenceExtractor makes once preflight has opened and
        // validated pdf: reads the native Info-dictionary (Title/Author/CreationDate),
        // the outline/bookmark tree, and logs both - then disposes pdf, taking over its
        // lifetime from the caller (hence the `using` here rather than in
        // DocumentIntelligenceExtractor). Distinct from Parse() above, which derives
        // Zenya's own Title/Version from the blob name and first-page text.
        public static DocMetadata ParseNativeMetadata(PdfDocument pdf, string blobName, ILogger logger)
        {
            using (pdf)
            {
                var info = pdf.Information;
                var bookmarks = GetBookmarks(pdf, blobName, logger);

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

        // Bookmarks/outline tree - PdfPig only, best-effort. No DI feature returns this
        // under any tier, but unlike most PdfPig reads, its bookmark-reading code can
        // throw PdfDocumentFormatException mid-tree-walk on a malformed outline node
        // (BookmarksProvider.ReadBookmarksRecursively) despite the Try-prefixed name on
        // TryGetBookmarks - it doesn't fully uphold the no-throw contract that name
        // implies. Bookmarks are a nice-to-have, not a hard requirement, so any failure
        // is caught and logged rather than allowed to fail the whole extraction.
        //
        // null = couldn't get bookmarks (PdfPig error) - distinct from an empty list,
        // which means extraction ran fine and this PDF genuinely has none. Callers
        // should treat null as "skip bookmarks for this document," not as a reason to
        // fail it.
        private static IReadOnlyList<Bookmark>? GetBookmarks(PdfDocument pdf, string blobName, ILogger logger)
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

        // DocumentBookmarkNode.PageNumber resolves to a page in *this* file.
        // ExternalBookmarkNode inherits DocumentBookmarkNode (confirmed by reflecting the
        // pinned 0.1.9 assembly - it is not a sibling type) and its PageNumber is a page
        // in the *external* file it points at (see its added FileName property) - not a
        // page in this document, so it must be excluded explicitly rather than caught by
        // the DocumentBookmarkNode pattern match. PdfPig's own #736
        // page-number-defaults-to-0 fix (PR #930) isn't in 0.1.9 (that's the
        // 0.1.9->0.1.10 diff), so an unresolvable destination on this version may omit
        // the node from the tree entirely rather than reliably reporting 0 - the >0
        // check below is a best-effort guard for the cases that do surface a value, not
        // a substitute for that fix.
        private static int? TryGetPageNumber(BookmarkNode node) =>
            node is ExternalBookmarkNode
                ? null
                : node is DocumentBookmarkNode doc && doc.PageNumber > 0
                    ? doc.PageNumber
                    : null;

        // Derives Zenya's own Title/Version/PublicationDate for a PDF — there's no
        // external index file for PDFs to join against, unlike Zenya's index.csv.
        // Shared by both IPdfExtractor backends so they parse metadata identically
        // regardless of which one is doing the text/table extraction.
        //
        // Previously matched Version/PublicationDateRaw (and overrode Title) via regexes
        // ported as-is from the PdfPig/Document Intelligence comparison spike's
        // LciMetadataParser - built against Dutch-language RIVM/LCI infectious-disease
        // guideline PDFs (a "Versie X.X" version marker, Dutch month names, a
        // "Title | LCI-richtlijn" heading format). Confirmed those don't apply to
        // Cordaan's own documents, so they were removed rather than left as dead
        // patterns that would never match - Title now only ever comes from the blob
        // name, and Version/PublicationDateRaw are left empty pending real Cordaan
        // conventions. firstPagesText is kept as a parameter (unused for now) since
        // whatever those real conventions turn out to be will likely need it again.
        public static PdfIndexRecord Parse(string blobName, string firstPagesText)
        {
            var title = blobName.Split('/')[0]
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
