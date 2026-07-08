using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// Instance (not static) specifically so encoding detection can log immediately at the
// point it happens, via an injected ILogger, instead of threading the detected encoding
// back out through ExtractionResult<T> for some other layer to log later.
public class CsvExtractor : ICsvExtractor
{
    private readonly ILogger<CsvExtractor> _logger;

    public CsvExtractor(ILogger<CsvExtractor> logger)
    {
        _logger = logger;
    }

    private static readonly CsvConfiguration Config = new(CultureInfo.InvariantCulture)
    {
        Delimiter       = ",",
        HasHeaderRecord = true,

        PrepareHeaderForMatch = args => args.Header.ToUpperInvariant(),
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

    public ExtractionResult<PageRecord> ExtractPages(Stream stream) =>
        Extract(stream, PagesRequiredHeaders, csv => new PageRecord
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


    // Reads zenya_index.csv row by row into IndexRecord objects - the document-level
    // metadata (title/version/summary/etc.) that CsvJoiner later attaches to every
    // page of the matching DOCUMENT_ID from ExtractPages. Same per-row error handling
    // as ExtractPages: a bad row is recorded and skipped, not fatal to the whole file.
    public ExtractionResult<IndexRecord> ExtractIndex(Stream stream) =>
        Extract(stream, IndexRequiredHeaders, csv => new IndexRecord
        {
            DocumentId        = RequireDocumentId(csv),
            DocumentTypeName  = csv.GetField("DOCUMENT_TYPE_NAME") ?? "",
            Summary           = csv.GetField("SUMMARY") ?? "",
            Version           = FormatVersion(csv),
            CheckDateRaw      = csv.GetField("CHECK_DATE") ?? "",
            AttentionFlagsRaw = csv.GetField("ATTENTION_REQUIRED_FLAGS") ?? "",
            Active            = ParseActive(csv),
        });

     // 1. Checks correct headers are there once, up front (EnsureHeadersAreCorrect)
    // 2. Per row, try to read it (csv.Read()):
    //      - throws        -> row is unparseable garbage; log an error (no DocumentId), keep going
    //      - returns false -> no more rows; stop
    //      - returns true  -> row read OK, go to step 3
    // 3. Try to build a PageRecord from that row's fields (needs DOCUMENT_ID + valid PAGE_INDEX):
    //      - succeeds -> add it as a PageRecord
    //      - fails    -> log it as an ExtractionError, with DocumentId if we could read one
    private ExtractionResult<T> Extract<T>(Stream stream, string[] requiredHeaders, Func<CsvReader, T> build)
    {
        var result = new ExtractionResult<T>();
        using var csv = EnsureHeadersAreCorrect(stream, requiredHeaders);

        var rowNumber = 1;
        while (true)
        {
            if (!EnsureRowIsReadable(csv, result, ref rowNumber)) break;

            rowNumber++;
            try
            {
                result.AddRecord(build(csv));
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

    // Repeatedly calls csv.Read() until it gets a definitive answer: true (there's a
    // So what this method actually does is:
// 1. Call csv.Read() — this performs the real read/tokenize work.
// 2. If it returns (either true = got a row, or false = EOF) → pass that straight back to the caller, done.
// 3. If it instead throws — meaning the row was too broken to even tokenize — catch it, log an ExtractionError, and loop back to step 1 to try the next row instead of giving up.
    private static bool EnsureRowIsReadable<T>(CsvReader csv, ExtractionResult<T> result, ref int rowNumber)
    {
        var failureStreak = 0;
        while (true)
        {
            try
            {
                return csv.Read(); //  read/tokenize work. either true = got a row, or false = EOF
            }
            catch (Exception ex)
            {
                rowNumber++;
                result.AddError(new ExtractionError { RowNumber = rowNumber, Message = $"Unreadable CSV row: {ex.Message}" });
                if (++failureStreak >= MaxConsecutiveReadFailures)
                    throw new InvalidOperationException(
                        $"{MaxConsecutiveReadFailures} consecutive unreadable rows around row {rowNumber} — input is not parseable CSV.", ex);
            }
        }
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

    private CsvReader EnsureHeadersAreCorrect(Stream stream, string[] requiredHeaders)
    {
        var streamReader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var csv = new CsvReader(streamReader, Config);
        csv.Read();       // advance to the header row - also triggers the encoding detection below
        csv.ReadHeader(); // capture it into HeaderRecord (one-time, not per data row)

        // Zenya's export is expected to always be UTF-8 (BOM or no BOM - both decode to
        // this same CodePage). Anything else means either the wrong file landed here or
        // the export process changed upstream in a way we've never seen - checked before
        // the header check below because a real encoding mismatch would otherwise surface
        // as a confusing "missing required column" error instead of naming the actual
        // cause. Proactive, not just logged: this fails the whole file immediately, the
        // same way a missing header does, rather than letting every row risk silent
        // mojibake/U+FFFD corruption for a cause we could have caught up front.
        if (streamReader.CurrentEncoding.CodePage != Encoding.UTF8.CodePage)
            throw new InvalidOperationException(
                $"CSV is encoded as '{streamReader.CurrentEncoding.WebName}', expected UTF-8.");

        _logger.LogInformation("CSV encoding detected: {Encoding}", streamReader.CurrentEncoding.WebName);

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
        var docId = (csv.GetField("DOCUMENT_ID") ?? "").Trim();
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


    private static bool ParseActive(CsvReader csv)
    {
        if (!csv.TryGetField<string>("ACTIVE", out var raw) || string.IsNullOrWhiteSpace(raw))
            return true;
        if (!bool.TryParse(raw, out var active))
            throw new FormatException($"ACTIVE '{raw}' is not a valid true/false value.");
        return active;
    }

}
