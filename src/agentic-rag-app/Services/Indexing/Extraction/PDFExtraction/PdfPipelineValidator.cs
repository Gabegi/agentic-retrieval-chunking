using System.Globalization;
using System.Text.RegularExpressions;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// Mirrors PipelineValidator.cs's checks and pass/fail algorithm exactly, adapted to
// the PDF models. Two differences from CSV's validator:
//   - No CheckDateExceeded red flag — Zenya's check_date_exceeded attention flag has
//     no PDF equivalent (no attention-flags data source for PDFs).
//   - One PDF-only addition: a table-flattening check (repeated-trigram heuristic,
//     ported from the PdfPig/Document Intelligence comparison spike's
//     TableFlatteningChecker) — a table collapsed into run-on prose is a failure mode
//     much more likely from a PDF backend than from Zenya's already-clean export.
public class PdfPipelineValidator : IPdfPipelineValidator
{
    private const double MaxAcceptableErrorRatePercent      = 1.0;
    private const double MaxAcceptableMagnitudeShiftPercent = 20.0;
    private const int    SpotCheckSampleSize                = 5;
    private const char   ReplacementChar                    = '�';

    private static readonly Regex MarkdownHeading =
        new(@"^#{1,6}\s", RegexOptions.Multiline | RegexOptions.Compiled);

    public PdfValidationReport Validate(
        ExtractionResult<PdfPageRecord>                   pagesExtraction,
        PdfCleanResult                                     cleanResult,
        int?                                               previousRunCleanedCount = null,
        IReadOnlyList<PdfExtractionDiagnostics>?           diagnostics = null,
        IReadOnlyDictionary<string, PdfDocumentStructure>? structures = null)
    {
        var redFlags = new List<string>();

        // 1. Collect all errors from the previous steps.
        var issues = CollectIssues(pagesExtraction, cleanResult);

        // 2. Check whether extraction page numbers reconcile through clean.
        var reconciliation = CheckExtractVsCleanCount(pagesExtraction, cleanResult);

        // 3. Magnitude shift vs a previous run, if supplied.
        var magnitude = CheckMagnitudeShift(cleanResult, previousRunCleanedCount);

        // 4. Text (char/encoding) quality issues.
        issues.AddRange(TextQualityCheck(cleanResult));

        // 4b. PDF-only: tables collapsed into repeated-phrase prose during extraction.
        issues.AddRange(TableFlatteningCheck(cleanResult));

        // 4c. Table structure issues, from DI's own table data - not a text-pattern guess.
        issues.AddRange(TableStructureQualityCheck(structures));

        // 5. Total tables detected this run — real count from DI's table detection,
        // trended over time, not gated.
        var detectedTableCount = CountDetectedTables(structures);

        // 6. docsNeedingFallback = zero headings across every single page of that document.
        var docsWithNoPagesWithHeadings = DocsWithNoPagesWithHeading(cleanResult);
        if (docsWithNoPagesWithHeadings.Count > 0)
            redFlags.Add($"{docsWithNoPagesWithHeadings.Count} document(s) have no markdown headings — need fallback chunking.");

        // 6b. Documents short enough that cross-page decoration (header/footer) detection
        // never ran at all - every line on those pages is kept as-is, decoration or not.
        // Currently never triggers: diagnostics is only ever non-empty when something
        // populates PdfFileExtraction.Diagnostics, which nothing does since the PdfPig
        // backend (the only producer) was removed.
        if (diagnostics is { Count: > 0 })
        {
            var noDecorationCount = diagnostics.Count(d => !d.DecorationDetectionRan);
            if (noDecorationCount > 0)
                redFlags.Add(
                    $"{noDecorationCount} document(s) got no header/footer stripping — too few pages for decoration detection.");
        }

        // 7. Takes a random sample for human review.
        var sample = BuildRandomCheckSample(cleanResult);

        // 8. Final pass/fail check.
        var errorCount     = issues.Count(i => i.Severity == "Error");
        var totalAttempted = pagesExtraction.RowsAttempted;
        var errorRate       = totalAttempted == 0 ? 100.0 : 100.0 * errorCount / totalAttempted;

        var passedExcludingMagnitude = errorRate <= MaxAcceptableErrorRatePercent && reconciliation.Count == 0;
        var passed                   = passedExcludingMagnitude && magnitude.Count == 0;

        return new PdfValidationReport
        {
            RunAtUtc                         = DateTime.UtcNow,
            PagesExtracted                   = pagesExtraction.Records.Count,
            CleanedRecords                   = cleanResult.Records.Count,
            Issues                           = issues,
            ReconciliationProblems           = reconciliation,
            MagnitudeWarnings                = magnitude,
            RedFlags                         = redFlags,
            SpotCheckSample                  = sample,
            DocumentsNeedingFallbackChunking = docsWithNoPagesWithHeadings,
            MojibakeRepairedPages            = cleanResult.MojibakeRepairedPages,
            DetectedTableCount               = detectedTableCount,
            Passed                           = passed,
            PassedExcludingMagnitude         = passedExcludingMagnitude,
        };
    }

    // 1. Aggregate every error/warning bucket into one place.
    private static List<ValidationIssue> CollectIssues(
        ExtractionResult<PdfPageRecord> pagesExtraction,
        PdfCleanResult                  cleanResult)
    {
        var issues = new List<ValidationIssue>();

        issues.AddRange(pagesExtraction.Errors.Select(e => new ValidationIssue
            { Stage = "Parse:Pages", Severity = "Error", DocumentId = e.DocumentId ?? "", Message = $"File {e.RowNumber}: {e.Message}", Reason = e.Reason }));

        issues.AddRange(pagesExtraction.Warnings.Select(w => new ValidationIssue
            { Stage = "Parse:Pages", Severity = "Warning", DocumentId = w.DocumentId ?? "", Message = w.Message }));

        issues.AddRange(cleanResult.Errors.Select(e => new ValidationIssue
            { Stage = "Clean", Severity = "Error", DocumentId = e.DocumentId, Message = e.Message }));

        issues.AddRange(cleanResult.Warnings.Select(w => new ValidationIssue
            { Stage = "Clean", Severity = "Warning", DocumentId = w.DocumentId, Message = w.Message }));

        return issues;
    }

    // 2. Every page that comes out of Extraction must land in exactly one Clean bucket:
    // cleanResult.Records.Count + cleanResult.Errors.Count + cleanResult.DuplicatePagesSkipped
    // == pagesExtraction.Records.Count (no join step in between anymore).
    private static List<string> CheckExtractVsCleanCount(
        ExtractionResult<PdfPageRecord> pagesExtraction,
        PdfCleanResult                  cleanResult)
    {
        var reconciliation = new List<string>();

        if (cleanResult.Records.Count + cleanResult.Errors.Count + cleanResult.DuplicatePagesSkipped
                != pagesExtraction.Records.Count)
            reconciliation.Add(
                $"Extract->Clean mismatch: {pagesExtraction.Records.Count} pages extracted, but " +
                $"{cleanResult.Records.Count} cleaned + {cleanResult.Errors.Count} errored + " +
                $"{cleanResult.DuplicatePagesSkipped} duplicate-skipped.");

        // Zero cleaned records is never a legitimate outcome — same rationale as CSV's
        // validator: the diff step downstream deletes anything "missing", so an
        // empty run must never sail through as a pass, first-run or not.
        if (cleanResult.Records.Count == 0)
            reconciliation.Add("Zero cleaned records produced — refusing to pass an empty run.");

        // Referential integrity: no duplicate (BlobName, PageIndex) in the final output.
        // Defense-in-depth, not reachable today — PdfCleaner already dedupes on this
        // exact key before a record ever reaches cleanResult.Records.
        var duplicateKeys = cleanResult.Records
            .GroupBy(r => (r.BlobName, r.PageIndex))
            .Where(g => g.Count() > 1)
            .Select(g => $"Duplicate output key: {g.Key.BlobName} / page {g.Key.PageIndex} appears {g.Count()} times");
        reconciliation.AddRange(duplicateKeys);

        return reconciliation;
    }

    // 3. Magnitude shift vs a previous run, if supplied.
    private static List<string> CheckMagnitudeShift(PdfCleanResult cleanResult, int? previousRunCleanedCount)
    {
        var magnitude = new List<string>();

        if (previousRunCleanedCount is int previous && previous > 0)
        {
            var deltaPercent = 100.0 * (cleanResult.Records.Count - previous) / previous;
            if (Math.Abs(deltaPercent) > MaxAcceptableMagnitudeShiftPercent)
                magnitude.Add(
                    $"Cleaned count shifted {deltaPercent:+0.0;-0.0}% vs previous run " +
                    $"({previous} -> {cleanResult.Records.Count}) — exceeds {MaxAcceptableMagnitudeShiftPercent}% threshold.");
        }

        return magnitude;
    }

    // 4. Text-quality signals — identical checks to CSV's validator, applied to PDF's
    // rendered markdown content.
    private static List<ValidationIssue> TextQualityCheck(PdfCleanResult cleanResult)
    {
        var issues = new List<ValidationIssue>();

        foreach (var record in cleanResult.Records)
        {
            var replacementCount = record.PageContent.Count(c => c == ReplacementChar);
            if (replacementCount > 0)
                issues.Add(new ValidationIssue { Stage = "TextQuality", Severity = "Error",
                    DocumentId = record.BlobName,
                    Message    = $"Page {record.PageIndex}: {replacementCount} U+FFFD char(s) — source text is corrupted." });

            var corruptCharCount = record.PageContent.Count(c =>
                c is not ('\n' or '\r' or '\t')
                && CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.OtherNotAssigned);
            if (corruptCharCount > 0)
                issues.Add(new ValidationIssue { Stage = "TextQuality", Severity = "Error",
                    DocumentId = record.BlobName,
                    Message    = $"Page {record.PageIndex}: {corruptCharCount} control/unassigned character(s) — likely encoding corruption." });
        }

        return issues;
    }

    // 4c. Table structure issues read directly off DI's own table data (PdfDocumentStructure.Tables) -
    // replaces a previous heuristic that pattern-matched GFM pipe-table syntax ("| a | b |")
    // in the rendered content. DI's markdown output actually renders tables as HTML
    // <table> elements, so that heuristic never matched real output and was silently a no-op.
    private static List<ValidationIssue> TableStructureQualityCheck(
        IReadOnlyDictionary<string, PdfDocumentStructure>? structures)
    {
        var issues = new List<ValidationIssue>();
        if (structures is null) return issues;

        foreach (var (blobName, structure) in structures)
        {
            foreach (var table in structure.Tables)
            {
                if (table.RowCount <= 0 || table.ColumnCount <= 0)
                {
                    issues.Add(new ValidationIssue { Stage = "TextQuality", Severity = "Warning",
                        DocumentId = blobName,
                        Message    = $"Table at offset {table.Offset}: reported {table.RowCount} row(s) x {table.ColumnCount} column(s) — malformed." });
                }
                else if (table.Cells.Count == 0)
                {
                    issues.Add(new ValidationIssue { Stage = "TextQuality", Severity = "Warning",
                        DocumentId = blobName,
                        Message    = $"Table at offset {table.Offset}: {table.RowCount}x{table.ColumnCount} reported but no cell data was extracted." });
                }
            }
        }

        return issues;
    }

    // 4b. PDF-only: detects tables that were collapsed into unstructured prose during
    // extraction. Signal: a 3-word phrase that repeats within a single page — a strong
    // indicator that structured row data was run together with no delimiters left.
    // Ported from the comparison spike's TableFlatteningChecker.
    private static List<ValidationIssue> TableFlatteningCheck(PdfCleanResult cleanResult)
    {
        var issues = new List<ValidationIssue>();

        foreach (var record in cleanResult.Records)
        {
            var repeated = FindRepeatedTrigrams(record.PageContent);
            if (repeated.Count == 0) continue;

            issues.Add(new ValidationIssue { Stage = "TableFlattening", Severity = "Warning",
                DocumentId = record.BlobName,
                Message    = $"Page {record.PageIndex}: possible flattened table — repeated phrase(s) {string.Join(", ", repeated.Take(3))}." });
        }

        return issues;
    }

    private static List<string> FindRepeatedTrigrams(string text)
    {
        var words = Regex.Replace(text.ToLowerInvariant(), @"[^\w\s]", " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 3) return [];

        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i <= words.Length - 3; i++)
        {
            var trigram = $"{words[i]} {words[i + 1]} {words[i + 2]}";
            seen[trigram] = seen.TryGetValue(trigram, out var count) ? count + 1 : 1;
        }

        return seen
            .Where(kv => kv.Value > 1)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"\"{kv.Key}\" ({kv.Value}x)")
            .ToList();
    }

    // 5. Total tables detected this run - real count from DI's own table detection.
    private static int CountDetectedTables(IReadOnlyDictionary<string, PdfDocumentStructure>? structures) =>
        structures?.Values.Sum(s => s.Tables.Count) ?? 0;

    // 6. Document flagged if none of its pages has a heading.
    private static List<string> DocsWithNoPagesWithHeading(PdfCleanResult cleanResult)
    {
        var docsWithHeadings = cleanResult.Records
            .Where(r => MarkdownHeading.IsMatch(r.PageContent))
            .Select(r => r.BlobName)
            .ToHashSet();

        return cleanResult.Records
            .Select(r => r.BlobName)
            .Distinct()
            .Where(id => !docsWithHeadings.Contains(id))
            .ToList();
    }

    // 7. Takes a random sample for human review.
    private static List<CleanedPdfPageRecord> BuildRandomCheckSample(PdfCleanResult cleanResult) =>
        cleanResult.Records.Count <= SpotCheckSampleSize
            ? [.. cleanResult.Records]
            : [.. cleanResult.Records.OrderBy(_ => Guid.NewGuid()).Take(SpotCheckSampleSize)];
}
