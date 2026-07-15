using System.Text.RegularExpressions;
using ProtocolsIndexer.Models;
using UglyToad.PdfPig;

namespace ProtocolsIndexer.Services;

// Parses a PDF file's own title/version/publication-date out of its filename and
// first-page text — there's no external index file for PDFs to join against, unlike
// Zenya's index.csv. Shared by both IPdfExtractor backends so they parse metadata
// identically regardless of which one is doing the text/table extraction.
//
// These patterns are ported as-is from the PdfPig/Document Intelligence comparison
// spike's LciMetadataParser (built against sample RIVM/LCI infectious-disease
// guideline PDFs) — a working example, not a final ruleset. They need to be
// recalibrated once real Cordaan PDF filename/heading conventions are confirmed.
internal static class PdfMetadataExtraction
{
    private static readonly Regex DutchDateRegex = new(
        @"\b(\d{1,2}[-\s](januari|februari|maart|april|mei|juni|juli|augustus|" +
        @"september|oktober|november|december)[-\s]\d{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ShortDateRegex = new(@"\b(\d{2}-\d{2}-\d{4})\b", RegexOptions.Compiled);
    private static readonly Regex VersionRegex    = new(@"[Vv]ersie\s+([\d.]+)",  RegexOptions.Compiled);

    // this is really BAD!
    private static readonly Regex TitleRegex      = new(@"^(.+?)\s*\|\s*LCI-richtlijn", RegexOptions.Multiline | RegexOptions.Compiled);

    public static PdfIndexRecord Parse(string blobName, string firstPagesText)
    {
        var title = blobName.Split('/')[0]
            .Replace(".pdf", "", StringComparison.OrdinalIgnoreCase)
            .Replace("-", " ");

        var titleMatch = TitleRegex.Match(firstPagesText);
        if (titleMatch.Success) title = titleMatch.Groups[1].Value.Trim();

        var publicationDateRaw = "";
        var dateMatch = DutchDateRegex.Match(firstPagesText);
        if (dateMatch.Success)
            publicationDateRaw = dateMatch.Value;
        else
        {
            var shortMatch = ShortDateRegex.Match(firstPagesText);
            if (shortMatch.Success) publicationDateRaw = shortMatch.Value;
        }

        var version = "";
        var versionMatch = VersionRegex.Match(firstPagesText);
        if (versionMatch.Success) version = versionMatch.Groups[1].Value;

        return new PdfIndexRecord
        {
            BlobName           = blobName,
            Title              = title,
            Version            = version,
            PublicationDateRaw = publicationDateRaw,
        };
    }

    // Native PDF Info-dictionary metadata (Title/Author/CreationDate) — distinct from
    // Parse() above, which derives Zenya's own Title/Version from the blob name and
    // first-page text. Used by DocumentIntelligenceExtractor, on an already-open
    // PdfDocument, so it never opens the file a second time.
    public static DocMetadata ParseNativeMetadata(PdfDocument pdf)
    {
        var info = pdf.Information;
        return new DocMetadata(
            Title:     string.IsNullOrWhiteSpace(info.Title)  ? null : info.Title,
            Author:    string.IsNullOrWhiteSpace(info.Author) ? null : info.Author,
            CreatedAt: TryParsePdfDate(info.CreationDate),
            PageCount: pdf.NumberOfPages);
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
