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

        // Turns off by default the two potential csv.Read Exceptions
        MissingFieldFound = null,
        BadDataFound      = null,   // malformed fields become row-level errors below, not exceptions
    };

    // csv.Read() itself can throw on rows the parser can't recover from; a bounded
    // failure streak distinguishes "one broken row" from "this is not a CSV" and
    // prevents looping forever if the parser can't advance past a corrupt region.
    private const int MaxConsecutiveReadFailures = 25;

    private static readonly string[] PagesRequiredHeaders;
    private static readonly string[] IndexRequiredHeaders;

    static CsvExtractor()
    {
        PagesRequiredHeaders = new[]
        {
            "DOCUMENT_ID", "TITLE", "QUICK_CODE", "FOLDER_MINI_FULL_PATH",
            "LAST_MODIFIED_DATETIME", "PAGE_INDEX", "PAGE_CONTENT", "RELATIVE_PATH",
        };
        IndexRequiredHeaders = new[]
        {
            "DOCUMENT_ID", "DOCUMENT_TYPE_NAME", "SUMMARY", "VERSION",
            "CHECK_DATE", "ATTENTION_REQUIRED_FLAGS",
        };
    }

    // 1. Checks correct headers are there once, up front (EnsureHeadersAreCorrect)
    // 2. Per row, try to read it (csv.Read()):
    //      - throws        -> row is unparseable garbage; log an error (no DocumentId), keep going
    //      - returns false -> no more rows; stop
    //      - returns true  -> row read OK, go to step 3
    // 3. Try to build a PageRecord from that row's fields (needs DOCUMENT_ID + valid PAGE_INDEX):
    //      - succeeds -> add it as a PageRecord
    //      - fails    -> log it as an ExtractionError, with DocumentId if we could read one
    public static ExtractionResult<PageRecord> ExtractPages(Stream stream)
    {
        var result = new ExtractionResult<PageRecord>();
        using var csv = EnsureHeadersAreCorrect(stream, PagesRequiredHeaders);

        int rowNumber = 1, failureStreak = 0;
        while (true) // always runs, even if one row fails
        {
            bool hasRow;
            try
            {
                // csv.Read() by default throws 2 exceptions:
                    // - A row has fewer fields than the header → MissingFieldException
                    // - A field contains malformed data (e.g. a stray unescaped quote inside an unquoted field) → BadDataException
                    // Turn off by default
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
                // Missing fields become "" rather than null (MissingFieldFound = null
                // in Config), except DOCUMENT_ID and PAGE_INDEX which are required
                // per-row since downstream joining/ordering depends on them.
                result.AddRecord(new PageRecord
                {
                    DocumentId      = RequireDocumentId(csv),
                    Title           = csv.GetField("TITLE") ?? "",
                    QuickCode       = csv.GetField("QUICK_CODE") ?? "",
                    FolderPath      = csv.GetField("FOLDER_MINI_FULL_PATH") ?? "",
                    LastModifiedRaw = csv.GetField("LAST_MODIFIED_DATETIME") ?? "",
                    PageIndex       = ParsePageIndex(csv),
                    PageContent     = csv.GetField("PAGE_CONTENT") ?? "",
                    Language        = csv.GetField("LANGUAGE") ?? "",
                    RelativePath    = csv.GetField("RELATIVE_PATH") ?? "",
                });
            }
            catch (Exception ex)
            {
                // Row-level failure (RequireDocumentId/ParsePageIndex throwing) - recorded,
                // then the loop moves on to the next row instead of aborting the file.
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

    // make sure that the document has the headers we expect
    //
    // 1. Construct the CsvReader, call csv.Read() once to advance onto the header row,
    //    then csv.ReadHeader() to capture that single row into HeaderRecord.
    // 2. Check that row (once) against requiredHeaders and throw if any are missing.
    // 3. Return the reader, positioned right after the header, ready for the caller's
    //    row loop to start calling csv.Read() for data rows.
    //
    // So it does two things: open/position the reader, and fail fast on a bad header.
    private static CsvReader EnsureHeadersAreCorrect(Stream stream, string[] requiredHeaders)
    {
        var csv = new CsvReader(new StreamReader(stream), Config);
        csv.Read();       // advance to the header row
        csv.ReadHeader(); // capture it into HeaderRecord (one-time, not per data row)

        var missing = requiredHeaders
            .Where(c => csv.HeaderRecord is null
                     || !csv.HeaderRecord.Contains(c, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"CSV header is missing required column(s): {string.Join(", ", missing)}.");

        return csv;
    }

    // Shared by both ExtractPages and ExtractIndex - DOCUMENT_ID is the join key
    // CsvJoiner matches pages to index rows on, so an empty one makes the row
    // useless downstream regardless of which file it came from.
    private static string RequireDocumentId(CsvReader csv)
    {
        var docId = csv.GetField("DOCUMENT_ID") ?? "";
        if (string.IsNullOrWhiteSpace(docId))
            throw new FormatException("DOCUMENT_ID is missing or empty.");
        return docId;
    }

    // PageRecord-only (ExtractIndex has no PAGE_INDEX). Pages need a numeric index
    // so DataCleaner/output ordering can sort a document's pages correctly.
    private static int ParsePageIndex(CsvReader csv)
    {
        var raw = csv.GetField("PAGE_INDEX");
        if (!int.TryParse(raw, out var value))
            throw new FormatException($"PAGE_INDEX '{raw}' is not a valid integer.");
        return value;
    }

    // "VERSION.REVISION" (e.g. "7.0"). REVISION isn't in the required-columns list — if it's
    // missing/unparseable, fall back to bare VERSION rather than failing the whole row over it.
    private static string FormatVersion(CsvReader csv)
    {
        var version = csv.GetField("VERSION") ?? "";
        if (string.IsNullOrWhiteSpace(version))
            return "";
        return int.TryParse(csv.GetField("REVISION"), out var revision) ? $"{version}.{revision}" : version;
    }

    // Reads zenya_index.csv row by row into IndexRecord objects - the document-level
    // metadata (title/version/summary/etc.) that CsvJoiner later attaches to every
    // page of the matching DOCUMENT_ID from ExtractPages. Same per-row error handling
    // as ExtractPages: a bad row is recorded and skipped, not fatal to the whole file.
    public static ExtractionResult<IndexRecord> ExtractIndex(Stream stream)
    {
        var result = new ExtractionResult<IndexRecord>();
        using var csv = EnsureHeadersAreCorrect(stream, IndexRequiredHeaders);

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
                // ACTIVE has no required-column entry - unlike a missing DOCUMENT_ID,
                // there's a safe default (treat unparseable/absent as active) rather
                // than a reason to fail the row.
                result.AddRecord(new IndexRecord
                {
                    DocumentId        = RequireDocumentId(csv),
                    DocumentTypeName  = csv.GetField("DOCUMENT_TYPE_NAME") ?? "",
                    Summary           = csv.GetField("SUMMARY") ?? "",
                    Version           = FormatVersion(csv),
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
