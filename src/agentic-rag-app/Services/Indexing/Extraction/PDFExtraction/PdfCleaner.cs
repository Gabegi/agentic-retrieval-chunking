using System.Text.RegularExpressions;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// Cleans extracted PDF page records: repairs known mojibake and collapses excess blank
// lines. Mirrors DataCleaner's structure and behavior. One bad page becomes a
// CleaningError; it never aborts the whole run. Deliberately has no opinion about
// duplicate (BlobName, PageIndex) pages — a genuine duplicate can only mean the
// extractor returned the same page twice, which is an invariant violation, not a
// content-quality issue to clean around. That's asserted once, in
// PdfPipelineValidator, not here.
public class PdfCleaner : IPdfCleaner
{
    // Collapse 3+ consecutive newlines down to a single blank line.
    private static readonly Regex ExcessBlankLines =
        new(@"\n{3,}", RegexOptions.Compiled);

    // Same class of Windows-1252/UTF-8 mis-decode as CSV's DataCleaner sees — PDF text
    // extraction can hit the same corruption if the backend mis-decodes embedded fonts.
    // Order matters: "â€" is a string-prefix of "â€™"/"â€œ"/"â€“"/"â€”", so it must be
    // checked LAST - otherwise it eats the first two characters of those longer patterns
    // before their own (more specific) match ever gets a chance to fire, leaving a stray
    // fallback character plus an unrepaired remainder instead of the real fix.
    private static readonly (string Pattern, string Fix)[] KnownMojibakePatterns =
    [
        ("â€™", "'"), ("â€œ", "\""), ("â€“", "–"), ("â€”", "—"), ("â€", "\""),
        ("Ã«", "ë"), ("Ã©", "é"), ("Ã¯", "ï"), ("Ã¼", "ü"),
    ];

    public PdfCleanResult Clean(IReadOnlyList<PdfPageRecord> pages)
    {
        var result = new PdfCleanResult();

        foreach (var page in pages)
            CleanSinglePage(page, result);

        return result;
    }

    private static void CleanSinglePage(PdfPageRecord page, PdfCleanResult result)
    {
        try
        {
            var (content, mojibakeFixed) = CleanPageContent(page.PageContent ?? "");

            if (mojibakeFixed)
            {
                result.CountMojibakeRepaired();
                result.AddWarning(new CleaningWarning
                {
                    DocumentId = page.BlobName,
                    Message    = $"Page {page.PageIndex}: repaired mojibake in source text (e.g. 'â€™' -> \"'\").",
                });
            }

            if (string.IsNullOrWhiteSpace(content))
                result.AddWarning(new CleaningWarning
                {
                    DocumentId = page.BlobName,
                    Message    = $"PageContent is empty after cleanup (page {page.PageIndex}) — likely a blank source page.",
                });

            result.AddRecord(ToCleanedRecord(page, content));
        }
        catch (Exception ex)
        {
            result.AddError(new CleaningError { DocumentId = page.BlobName, Message = ex.Message });
        }
    }

    private static CleanedPdfPageRecord ToCleanedRecord(PdfPageRecord page, string content) => new()
    {
        BlobName    = page.BlobName,
        PageIndex   = page.PageIndex,
        PageContent = content,
        Title       = TrimOrEmpty(page.Title),
    };

    private static string TrimOrEmpty(string? value) => value?.Trim() ?? "";

    // Normalizes page text: normalize line endings, repair known mojibake, collapse
    // excess blank lines, trim. No boilerplate/logo stripping yet — Cordaan's PDF
    // header/footer conventions aren't confirmed; slot new regexes in here once
    // real sample PDFs are available.
    private static (string Content, bool MojibakeFixed) CleanPageContent(string raw)
    {
        var text = raw.Replace("\r\n", "\n").Replace("\r", "\n");

        var mojibakeFixed = false;
        foreach (var (pattern, fix) in KnownMojibakePatterns)
        {
            if (!text.Contains(pattern)) continue;
            text          = text.Replace(pattern, fix);
            mojibakeFixed = true;
        }

        text = ExcessBlankLines.Replace(text, "\n\n");
        return (text.Trim(), mojibakeFixed);
    }
}
