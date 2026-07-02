using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public static class DataCleaner
{
    private static readonly Regex CordaanBoilerplate =
        new(@"^cordaan[ \t]*$\n?", RegexOptions.Multiline | RegexOptions.Compiled);
        // standalone logo lines only; case-sensitive, intentionally — never touches "Cordaan" in prose
    private static readonly Regex ImagePlaceholder =
        new(@"!\[[^\]]*\]\([^\)]*\)", RegexOptions.Compiled);
    private static readonly Regex ExcessBlankLines =
        new(@"\n{3,}", RegexOptions.Compiled);

    public static CleanResult Clean(IReadOnlyList<JoinedPageRecord> pages)
    {
        var result = new CleanResult();

        foreach (var page in pages)
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

                result.AddRecord(new CleanedPageRecord
                {
                    DocumentId       = page.DocumentId,
                    Title            = page.Title.Trim(),
                    QuickCode        = page.QuickCode.Trim(),
                    FolderPath       = page.FolderPath.Trim(),
                    LastModified     = ParseDateTime(page.LastModifiedRaw, "yyyyMMddHHmmss", page.DocumentId, "LastModifiedRaw"),
                    PageIndex        = page.PageIndex,
                    PageContent      = content,
                    DocumentTypeName = page.DocumentTypeName.Trim(),
                    Summary          = page.Summary.Trim(),
                    Version          = page.Version.Trim(),
                    CheckDate        = ParseOptionalDateTime(page.CheckDateRaw, "yyyyMMdd", page.DocumentId, "CheckDateRaw"),
                    AttentionFlags   = ParseAttentionFlags(page.AttentionFlagsRaw, page.DocumentId),
                });
            }
            catch (Exception ex)
            {
                result.AddError(new CleaningError { DocumentId = page.DocumentId, Message = ex.Message });
            }
        }

        return result;
    }

    private static string CleanPageContent(string raw)
    {
        var text = WebUtility.HtmlDecode(raw);
        text = text.Replace("\r\n", "\n");             // normalize before any \n-dependent regex
        text = CordaanBoilerplate.Replace(text, "");
        text = ImagePlaceholder.Replace(text, "[image]");
        text = ExcessBlankLines.Replace(text, "\n\n");
        return text.Trim();
    }

    private static DateTime ParseDateTime(string raw, string format, string documentId, string field)
    {
        if (!DateTime.TryParseExact(raw, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            throw new FormatException($"{field} '{raw}' on document {documentId} is not a valid '{format}' date.");
        return value;
    }

    private static DateTime? ParseOptionalDateTime(string raw, string format, string documentId, string field)
        => string.IsNullOrWhiteSpace(raw) ? null : ParseDateTime(raw, format, documentId, field);

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
