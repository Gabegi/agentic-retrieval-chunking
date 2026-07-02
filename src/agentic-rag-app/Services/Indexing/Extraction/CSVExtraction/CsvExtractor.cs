using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public static class CsvExtractor
{
    private static readonly CsvConfiguration Config = new(CultureInfo.InvariantCulture)
    {
        Delimiter         = ",",
        HasHeaderRecord   = true,
        MissingFieldFound = null,
        BadDataFound      = null,   // malformed fields become row-level errors below, not exceptions
    };

    // csv.Read() itself can throw on rows the parser can't recover from; a bounded
    // failure streak distinguishes "one broken row" from "this is not a CSV" and
    // prevents looping forever if the parser can't advance past a corrupt region.
    private const int MaxConsecutiveReadFailures = 25;

    private static CsvReader OpenCsv(Stream stream, params string[] requiredColumns)
    {
        var csv = new CsvReader(new StreamReader(stream), Config);
        csv.Read();
        csv.ReadHeader();

        // One clear failure beats 11k identical "DOCUMENT_ID is missing" row errors.
        var missing = requiredColumns
            .Where(c => csv.HeaderRecord is null
                     || !csv.HeaderRecord.Contains(c, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"CSV header is missing required column(s): {string.Join(", ", missing)}.");

        return csv;
    }

    private static string RequireDocumentId(CsvReader csv)
    {
        var docId = csv.GetField("DOCUMENT_ID") ?? "";
        if (string.IsNullOrWhiteSpace(docId))
            throw new FormatException("DOCUMENT_ID is missing or empty.");
        return docId;
    }

    private static int ParsePageIndex(CsvReader csv)
    {
        var raw = csv.GetField("PAGE_INDEX");
        if (!int.TryParse(raw, out var value))
            throw new FormatException($"PAGE_INDEX '{raw}' is not a valid integer.");
        return value;
    }

    public static ExtractionResult<PageRecord> ExtractPages(Stream stream)
    {
        var result = new ExtractionResult<PageRecord>();
        using var csv = OpenCsv(stream,
            "DOCUMENT_ID", "TITLE", "QUICK_CODE", "FOLDER_MINI_FULL_PATH",
            "LAST_MODIFIED_DATETIME", "PAGE_INDEX", "PAGE_CONTENT");

        int rowNumber = 1, failureStreak = 0;
        while (true)
        {
            bool hasRow;
            try
            {
                hasRow = csv.Read();
                failureStreak = 0;
            }
            catch (Exception ex)
            {
                rowNumber++;
                result.AddError(new ExtractionError { RowNumber = rowNumber, Message = $"Unreadable CSV row: {ex.Message}" });
                if (++failureStreak >= MaxConsecutiveReadFailures)
                    throw new InvalidOperationException(
                        $"{MaxConsecutiveReadFailures} consecutive unreadable rows around row {rowNumber} — input is not parseable CSV.", ex);
                continue;
            }
            if (!hasRow) break;

            rowNumber++;
            try
            {
                result.AddRecord(new PageRecord
                {
                    DocumentId      = RequireDocumentId(csv),
                    Title           = csv.GetField("TITLE") ?? "",
                    QuickCode       = csv.GetField("QUICK_CODE") ?? "",
                    FolderPath      = csv.GetField("FOLDER_MINI_FULL_PATH") ?? "",
                    LastModifiedRaw = csv.GetField("LAST_MODIFIED_DATETIME") ?? "",
                    PageIndex       = ParsePageIndex(csv),
                    PageContent     = csv.GetField("PAGE_CONTENT") ?? "",
                });
            }
            catch (Exception ex)
            {
                result.AddError(new ExtractionError
                {
                    RowNumber  = rowNumber,
                    DocumentId = csv.TryGetField<string>("DOCUMENT_ID", out var id) ? id : null,
                    Message    = ex.Message,
                });
            }
        }

        return result;
    }

    public static ExtractionResult<IndexRecord> ExtractIndex(Stream stream)
    {
        var result = new ExtractionResult<IndexRecord>();
        using var csv = OpenCsv(stream,
            "DOCUMENT_ID", "DOCUMENT_TYPE_NAME", "SUMMARY", "VERSION",
            "CHECK_DATE", "ATTENTION_REQUIRED_FLAGS");

        int rowNumber = 1, failureStreak = 0;
        while (true)
        {
            bool hasRow;
            try
            {
                hasRow = csv.Read();
                failureStreak = 0;
            }
            catch (Exception ex)
            {
                rowNumber++;
                result.AddError(new ExtractionError { RowNumber = rowNumber, Message = $"Unreadable CSV row: {ex.Message}" });
                if (++failureStreak >= MaxConsecutiveReadFailures)
                    throw new InvalidOperationException(
                        $"{MaxConsecutiveReadFailures} consecutive unreadable rows around row {rowNumber} — input is not parseable CSV.", ex);
                continue;
            }
            if (!hasRow) break;

            rowNumber++;
            try
            {
                result.AddRecord(new IndexRecord
                {
                    DocumentId        = RequireDocumentId(csv),
                    DocumentTypeName  = csv.GetField("DOCUMENT_TYPE_NAME") ?? "",
                    Summary           = csv.GetField("SUMMARY") ?? "",
                    Version           = csv.GetField("VERSION") ?? "",
                    CheckDateRaw      = csv.GetField("CHECK_DATE") ?? "",
                    AttentionFlagsRaw = csv.GetField("ATTENTION_REQUIRED_FLAGS") ?? "",
                    Active            = !bool.TryParse(csv.GetField("ACTIVE"), out var active) || active,  // unparseable/missing → assume active
                });
            }
            catch (Exception ex)
            {
                result.AddError(new ExtractionError
                {
                    RowNumber  = rowNumber,
                    DocumentId = csv.TryGetField<string>("DOCUMENT_ID", out var id) ? id : null,
                    Message    = ex.Message,
                });
            }
        }

        return result;
    }
}
