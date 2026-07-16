using System.Text.RegularExpressions;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// Cleans extracted PDF page records: repairs known mojibake, collapses excess blank
// lines, and de-duplicates pages. Mirrors DataCleaner's structure and behavior, but is
// entirely self-contained — nothing here is shared with CSVExtraction/, so that
// already-shipped pipeline is never touched by PDF work. One bad page becomes a
// CleaningError; it never aborts the whole run.
public class PdfCleaner : IPdfCleaner
{
    // Collapse 3+ consecutive newlines down to a single blank line.
    private static readonly Regex ExcessBlankLines =
        new(@"\n{3,}", RegexOptions.Compiled);

    // Same class of Windows-1252/UTF-8 mis-decode as CSV's DataCleaner sees — PDF text
    // extraction can hit the same corruption if the backend mis-decodes embedded fonts.
    private static readonly (string Pattern, string Fix)[] KnownMojibakePatterns =
    [
        ("â€™", "'"), ("â€œ", "\""), ("â€", "\""), ("â€“", "–"), ("â€”", "—"),
        ("Ã«", "ë"), ("Ã©", "é"), ("Ã¯", "ï"), ("Ã¼", "ü"),
    ];

    public PdfCleanResult Clean(IReadOnlyList<PdfPageRecord> pages)
    {
        var result   = new PdfCleanResult();
        var seenKeys = new HashSet<(string BlobName, int Page)>();

        foreach (var page in pages)
        {
            if (!seenKeys.Add((page.BlobName, page.PageIndex)))
            {
                ReportDuplicatePage(page, result);
                continue;
            }

            CleanSinglePage(page, result);
        }

        return result;
    }

    private static void ReportDuplicatePage(PdfPageRecord page, PdfCleanResult result)
    {
        result.CountDuplicateSkipped();
        result.AddWarning(new CleaningWarning
        {
            DocumentId = page.BlobName,
            Message    = $"Duplicate page {page.PageIndex} in source — kept the first occurrence.",
        });
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
