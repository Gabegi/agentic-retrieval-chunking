using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// Cleans joined page records: strips boilerplate/markup from content,
// parses raw string fields into typed values, and de-duplicates pages.
// One bad page becomes a CleaningError; it never aborts the whole run.
public static class DataCleaner
{
    // Standalone "cordaan"/"CORDAAN" logo lines only — lowercase and all-caps are
    // both confirmed logo text; mixed-case "Cordaan" is left untouched since that's
    // where real prose (org charts, sentences) shows up.
    private static readonly Regex CordaanBoilerplate =
        new(@"^(cordaan|CORDAAN)[ \t]*$\n?", RegexOptions.Multiline | RegexOptions.Compiled);

    // Markdown image placeholders, e.g. ![alt](path) — carry no text value.
    private static readonly Regex ImagePlaceholder =
        new(@"!\[[^\]]*\]\([^\)]*\)", RegexOptions.Compiled);

    // Literal HTML/XML tags left over after decoding entities (e.g. <br/>,
    // <concept factuur="" ...>) — HtmlDecode only unescapes entities, it doesn't
    // strip markup. Requires the first character after '<' (or '</') to be a letter
    // so stray comparison operators like "a < b" or "1 < 2 > 3" aren't touched.
    private static readonly Regex HtmlTag =
        new(@"<\/?[a-zA-Z][^<>]*>", RegexOptions.Compiled);

    // Collapse 3+ consecutive newlines down to a single blank line.
    private static readonly Regex ExcessBlankLines =
        new(@"\n{3,}", RegexOptions.Compiled);

    // Entry point. Walks all pages, skipping duplicates (same DocumentId +
    // PageIndex) and converting each remaining page to a CleanedPageRecord.
    // Parse failures are collected as errors; empty content only warns.
    public static CleanResult Clean(IReadOnlyList<JoinedPageRecord> pages)
    {
        var result   = new CleanResult();
        var seenKeys = new HashSet<(string DocId, int Page)>();

        foreach (var page in pages)
        {
            if (!seenKeys.Add((page.DocumentId, page.PageIndex)))
            {
                ReportDuplicatePage(page, result);
                continue;
            }

            CleanSinglePage(page, result);
        }

        return result;
    }

        // Records a skipped duplicate page (first occurrence wins).
    private static void ReportDuplicatePage(JoinedPageRecord page, CleanResult result)
    {
        result.CountDuplicateSkipped();
        result.AddWarning(new CleaningWarning
        {
            DocumentId = page.DocumentId,
            Message    = $"Duplicate page {page.PageIndex} in source — kept the first occurrence.",
        });
    }

    // Cleans one page and adds it to the result.
    // Any parse failure (dates, flags) lands in result.Errors for that page only.
    private static void CleanSinglePage(JoinedPageRecord page, CleanResult result)
    {
        try
        {
            var content = CleanPageContent(page.PageContent);

            if (string.IsNullOrWhiteSpace(content))
                result.AddWarning(new CleaningWarning
                {
                    DocumentId = page.DocumentId,
                    Message    = $"PageContent is empty after cleanup (page {page.PageIndex}) — likely a blank source page.",
                });

            result.AddRecord(ToCleanedRecord(page, content));
        }
        catch (Exception ex)
        {
            result.AddError(new CleaningError { DocumentId = page.DocumentId, Message = ex.Message });
        }
    }



    // Maps a joined record to its cleaned, typed equivalent:
    // trims string fields, parses dates and attention flags.
    private static CleanedPageRecord ToCleanedRecord(JoinedPageRecord page, string content) => new()
    {
        DocumentId       = page.DocumentId,
        Title            = page.Title.Trim(),
        QuickCode        = page.QuickCode.Trim(),
        FolderPath       = page.FolderPath.Trim(),
        LastModified     = ParseDateTime(page.LastModifiedRaw, "yyyyMMddHHmmss", page.DocumentId, "LastModifiedRaw"),
        PageIndex        = page.PageIndex,
        PageContent      = content,
        Language         = page.Language.Trim(),
        RelativePath     = page.RelativePath.Trim(),
        DocumentTypeName = page.DocumentTypeName.Trim(),
        Summary          = page.Summary.Trim(),
        Version          = page.Version.Trim(),
        CheckDate        = ParseOptionalDateTime(page.CheckDateRaw, "yyyyMMdd", page.DocumentId, "CheckDateRaw"),
        AttentionFlags   = ParseAttentionFlags(page.AttentionFlagsRaw, page.DocumentId),
    };

    // Normalizes page text: HTML-decode, normalize line endings (before any
    // \n-dependent regex), strip logo lines, image placeholders and leftover HTML
    // tags, collapse excess blank lines, trim.
    private static string CleanPageContent(string raw)
    {
        var text = WebUtility.HtmlDecode(raw);
        text = text.Replace("\r\n", "\n");
        text = CordaanBoilerplate.Replace(text, "");
        text = ImagePlaceholder.Replace(text, "");
        text = HtmlTag.Replace(text, "");
        text = ExcessBlankLines.Replace(text, "\n\n");
        return text.Trim();
    }

    // Parses a required date in the given exact format; throws FormatException
    // with document context if it doesn't match.
    private static DateTime ParseDateTime(string raw, string format, string documentId, string field)
    {
        if (!DateTime.TryParseExact(raw, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            throw new FormatException($"{field} '{raw}' on document {documentId} is not a valid '{format}' date.");
        return value;
    }

    // Same as ParseDateTime, but empty/whitespace input means "no date" (null).
    private static DateTime? ParseOptionalDateTime(string raw, string format, string documentId, string field)
        => string.IsNullOrWhiteSpace(raw) ? null : ParseDateTime(raw, format, documentId, field);

    // Parses AttentionFlagsRaw as a JSON string array; empty input → empty list,
    // invalid JSON → FormatException with document context.
    private static List<string> ParseAttentionFlags(string raw, string documentId)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(raw) ?? [];
        }
        catch (JsonException ex)
        {
            throw new FormatException($"AttentionFlagsRaw '{raw}' on document {documentId} is invalid JSON: {ex.Message}");
        }
    }
}