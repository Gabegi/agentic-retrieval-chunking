using System.Globalization;
using System.Text.RegularExpressions;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// Quality gate between extraction and the index. Mirrors PipelineValidator.cs's checks
// and pass/fail algorithm, adapted to the PDF models.
//
// The checks fall into three tiers — the tier decides where a finding lands:
//   HARD GATES  (-> ReconciliationProblems / MagnitudeWarnings; fail the run):
//     pipeline invariants and corpus-level sanity. These exist because the diff step
//     downstream DELETES whatever is missing from a passed run — a bad run that
//     passes doesn't just index garbage, it removes good documents from the index.
//   QUALITY ISSUES (-> Issues; gate only via the aggregate error-rate threshold):
//     per-page signals (encoding corruption, malformed tables).
//   ADVISORY (-> RedFlags / report fields; never gate):
//     trends and chunking hints (table counts, heading coverage, spot-check sample).
//
// Differences from CSV's validator:
//   - No CheckDateExceeded red flag — no attention-flags data source for PDFs.
//   - PDF-only: table-flattening heuristic (see TableFlatteningCheck) and table
//     structure checks read from DI's own table data.
public class PdfPipelineValidator : IPdfPipelineValidator
{
    private const double MaxAcceptableErrorRatePercent      = 1.0;
    private const double MaxAcceptableMagnitudeShiftPercent = 20.0;
    private const int    SpotCheckSampleSize                = 5;
    private const char   ReplacementChar                    = '�';

    // A trigram must repeat at least this many times on one page before it's flagged as
    // possible table flattening. 2 was the spike's value and false-positives on
    // legitimately repetitive protocol prose ("de cliënt moet ..."); 3 trades a little
    // sensitivity for a lot less noise. Tune against real runs — if the flattening
    // warnings are still mostly wolf-crying at 3, delete the whole check rather than
    // keep raising this.
    private const int MinTrigramRepeats = 3;

    // Pages shorter than this can't meaningfully contain a flattened table; skip them.
    private const int MinWordsForFlatteningCheck = 30;

    private static readonly Regex MarkdownHeading =
        new(@"^#{1,6}\s", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex NonWordChars =
        new(@"[^\w\s]", RegexOptions.Compiled);

    public PdfValidationReport Validate(
        IReadOnlyList<PDFExtractionResult>        fileResults,
        PdfCleanResult                             cleanResult,
        int?                                       previousRunCleanedCount = null,
        IReadOnlyList<PdfExtractionDiagnostics>?   diagnostics = null)
    {
        //1. Puts things into 3 buckets:
            // - Records = pages from files that extracted successfully.
            // - Errors = either a whole file that failed (file.Error, counted once) or individual bad pages within an otherwise-successful file (file.PageErrors).
            // - Warnings = non-fatal issues from successful files (file.Warnings).
        var pagesExtraction = SortResultsInto3Buckets(fileResults);

        // 2. dictionary of document structure per blob name (key)
        var (structures, similarNamingProblems) = GetDocumentStructure(fileResults);

        var redFlags = new List<string>();

        // 3. Collect all errors issues from two sources
            // - Extraction
            // - Cleaning
        var issues = GetIssuesFromExtractionNCleaning(pagesExtraction, cleanResult);

        // 4. HARD GATE: extraction page counts must reconcile through clean.
        var reconciliation = CheckDiffExtractNCleaning(pagesExtraction, cleanResult);
        reconciliation.AddRange(similarNamingProblems);

        // 5. HARD GATE (overridable): magnitude shift vs a previous run, if supplied.
        var magnitude = CheckMagnitudeShift(cleanResult, previousRunCleanedCount);

        // 7. Per-page text quality (U+FFFD, control/unassigned chars).
        issues.AddRange(TextQualityCheck(cleanResult));

        // 7b. PDF-only: tables collapsed into repeated-phrase prose during extraction.
        issues.AddRange(TableFlatteningCheck(cleanResult, structures));

        // 7c. Table structure issues, from DI's own table data — not a text-pattern guess.
        issues.AddRange(TableStructureQualityCheck(structures));

        // 8. ADVISORY: total tables detected this run — trended over time, not gated.
        var detectedTableCount = CountDetectedTables(structures);

        // 9. ADVISORY: documents with zero headings across every page need fallback chunking.
        var docsWithNoPagesWithHeadings = DocsWithNoPagesWithHeading(cleanResult);
        if (docsWithNoPagesWithHeadings.Count > 0)
            redFlags.Add($"{docsWithNoPagesWithHeadings.Count} document(s) have no markdown headings — need fallback chunking.");

        // 9b. ADVISORY, currently dormant: only fires if a backend populates
        // PdfFileExtraction.Diagnostics again (nothing does since PdfPig was removed).
        // Kept as the report slot for whichever backend picks decoration detection back up.
        if (diagnostics is { Count: > 0 })
        {
            var noDecorationCount = diagnostics.Count(d => !d.DecorationDetectionRan);
            if (noDecorationCount > 0)
                redFlags.Add(
                    $"{noDecorationCount} document(s) got no header/footer stripping — too few pages for decoration detection.");
        }

        // 10. ADVISORY: random sample for human review.
        var sample = BuildRandomCheckSample(cleanResult);

        // 11. Final pass/fail. Error rate is per ATTEMPTED page, so file-level failures
        // (which contribute errors but no pages) still count against the denominator.
        var errorCount     = issues.Count(i => i.Severity == "Error");
        var totalAttempted = pagesExtraction.RowsAttempted;
        var errorRate      = totalAttempted == 0 ? 100.0 : 100.0 * errorCount / totalAttempted;

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

        // Folds per-file PDFExtractionResult results into the batch-level shape the checks
    // operate on. A file-level extraction error is recorded once; a file that failed to
    // parse contributes nothing else. Validator-private on purpose — nothing but
    // validation bookkeeping needs this exact shape.
    private static ExtractionResult<PdfPageRecord> SortResultsInto3Buckets(IEnumerable<PDFExtractionResult> fileResults)
    {
        var pages = new ExtractionResult<PdfPageRecord>();

        foreach (var file in fileResults)
        {
            // if failed file has no pages, it's null
            // without this check the Add fails
            if (file.Error != null)
            {
                pages.AddError(file.Error);
                continue;
            }

            foreach (var page in file.Pages!) pages.AddRecord(page); // Ok=true guarantees Pages is populated
            foreach (var pageError in file.PageErrors) pages.AddError(pageError);
            foreach (var warning in file.Warnings) pages.AddWarning(warning);
        }

        return pages;
    }



    // 2. Azure Blob Storage allows both "Report.pdf" and "report.pdf" in the same container,
    // but this lookup is case-insensitive, so they'd collide. ToDictionary would throw and
    // crash the run on that collision; TryAdd below just logs it as a reconciliation
    // problem instead.
    private static (Dictionary<string, PdfDocumentStructure> Structures, List<string> CollisionProblems) GetDocumentStructure(
        IReadOnlyList<PDFExtractionResult> fileResults)
    {
        var structures        = new Dictionary<string, PdfDocumentStructure>(StringComparer.OrdinalIgnoreCase);
        var collisionProblems = new List<string>();

        foreach (var file in fileResults.Where(f => f.Structure != null))
        {
            if (!structures.TryAdd(file.BlobName, file.Structure!))
                collisionProblems.Add(
                    $"Blob name '{file.BlobName}' collides case-insensitively with another blob in this run — structure data for one was dropped.");
        }

        return (structures, collisionProblems);
    }

    // 3. Aggregate every error/warning bucket into one place. DocumentId (blob name)
    // identifies the file; RowNumber is a CSV concept and never set for PDFs.
    private static List<ValidationIssue> GetIssuesFromExtractionNCleaning(
        ExtractionResult<PdfPageRecord> pagesExtraction,
        PdfCleanResult                  cleanResult)
    {
        var issues = new List<ValidationIssue>();

        issues.AddRange(pagesExtraction.Errors.Select(e => new ValidationIssue
            { Stage = "Parse:Pages", Severity = "Error", DocumentId = e.DocumentId ?? "", Message = e.Message, Reason = e.Reason }));

        issues.AddRange(pagesExtraction.Warnings.Select(w => new ValidationIssue
            { Stage = "Parse:Pages", Severity = "Warning", DocumentId = w.DocumentId ?? "", Message = w.Message }));

        issues.AddRange(cleanResult.Errors.Select(e => new ValidationIssue
            { Stage = "Clean", Severity = "Error", DocumentId = e.DocumentId, Message = e.Message }));

        issues.AddRange(cleanResult.Warnings.Select(w => new ValidationIssue
            { Stage = "Clean", Severity = "Warning", DocumentId = w.DocumentId, Message = w.Message }));

        return issues;
    }

    // 2. Every extracted page must land in exactly one Clean bucket, an empty run never
    // passes (the diff step would delete the entire index), and the extractor must not
    // produce duplicate (BlobName, PageIndex) pairs. Duplicates land in reconciliation
    // (not Issues) so no error-rate threshold can let them slip through — this is the
    // sole enforcement of that invariant, checked against pagesExtraction so the
    // "extractor" attribution stays honest regardless of what Clean does.
    private static List<string> CheckDiffExtractNCleaning(
        ExtractionResult<PdfPageRecord> pagesExtraction,
        PdfCleanResult                  cleanResult)
    {
        var reconciliation = new List<string>();

        if (cleanResult.Records.Count + cleanResult.Errors.Count != pagesExtraction.Records.Count)
            reconciliation.Add(
                $"Extract->Clean mismatch: {pagesExtraction.Records.Count} pages extracted, but " +
                $"{cleanResult.Records.Count} cleaned + {cleanResult.Errors.Count} errored.");

        if (cleanResult.Records.Count == 0)
            reconciliation.Add("Zero cleaned records produced — refusing to pass an empty run.");

        reconciliation.AddRange(pagesExtraction.Records
            .GroupBy(r => (r.BlobName, r.PageIndex))
            .Where(g => g.Count() > 1)
            .Select(g => $"Duplicate page from extractor: {g.Key.BlobName} / page {g.Key.PageIndex} appears {g.Count()} times"));

        return reconciliation;
    }

    // 3. Magnitude shift vs a previous run, if supplied.
    // Design constraint for a future content-hash extraction skip: cleanResult only
    // reflects files actually (re-)extracted this run. If skipped-unchanged files stop
    // contributing to the count without their prior page counts being folded back in,
    // the first hash-skip run looks like a massive drop and trips this gate for a corpus
    // that didn't shrink. Resolve before shipping hash-skip, not after.
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

    // 4. Per-page text-quality signals. Single pass per page: both counters in one
    // walk of the content instead of two full Count() scans.
    private static List<ValidationIssue> TextQualityCheck(PdfCleanResult cleanResult)
    {
        var issues = new List<ValidationIssue>();

        foreach (var record in cleanResult.Records)
        {
            int replacementCount = 0, corruptCharCount = 0;

            foreach (var c in record.PageContent)
            {
                if (c == ReplacementChar) { replacementCount++; continue; }
                if (c is '\n' or '\r' or '\t') continue;
                if (CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.OtherNotAssigned)
                    corruptCharCount++;
            }

            if (replacementCount > 0)
                issues.Add(new ValidationIssue { Stage = "TextQuality", Severity = "Error",
                    DocumentId = record.BlobName,
                    Message    = $"Page {record.PageIndex}: {replacementCount} U+FFFD char(s) — source text is corrupted." });

            if (corruptCharCount > 0)
                issues.Add(new ValidationIssue { Stage = "TextQuality", Severity = "Error",
                    DocumentId = record.BlobName,
                    Message    = $"Page {record.PageIndex}: {corruptCharCount} control/unassigned character(s) — likely encoding corruption." });
        }

        return issues;
    }

    // 4b. PDF-only heuristic: a trigram repeating MinTrigramRepeats+ times on one page
    // suggests table rows run together with no delimiters left. Skips pages where DI
    // already detected a table — this check's purpose is tables DI MISSED; running it on
    // detected-table pages just false-positives on legitimate repeated cell content.
    // Skips short pages entirely. Warning-only: it gates nothing, so its only cost is
    // report noise — watch it in real runs and delete it if it stays noisy (see
    // MinTrigramRepeats).
    private static List<ValidationIssue> TableFlatteningCheck(
        PdfCleanResult cleanResult, IReadOnlyDictionary<string, PdfDocumentStructure>? structures)
    {
        var issues = new List<ValidationIssue>();

        foreach (var record in cleanResult.Records)
        {
            var hasDetectedTable = structures != null
                && structures.TryGetValue(record.BlobName, out var structure)
                && structure.Tables.Any(t => t.PageNumber == record.PageIndex);
            if (hasDetectedTable) continue;

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
        var words = NonWordChars.Replace(text.ToLowerInvariant(), " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < MinWordsForFlatteningCheck) return [];

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i <= words.Length - 3; i++)
        {
            var trigram = $"{words[i]} {words[i + 1]} {words[i + 2]}";
            seen[trigram] = seen.TryGetValue(trigram, out var count) ? count + 1 : 1;
        }

        return seen
            .Where(kv => kv.Value >= MinTrigramRepeats)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"\"{kv.Key}\" ({kv.Value}x)")
            .ToList();
    }

    // 4c. Table structure issues read directly off DI's own table data. Replaces an
    // earlier heuristic that pattern-matched GFM pipe tables — DI renders tables as
    // HTML <table> elements, so that heuristic never matched and was silently a no-op.
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
                    issues.Add(new ValidationIssue { Stage = "TextQuality", Severity = "Warning",
                        DocumentId = blobName,
                        Message    = $"Table at offset {table.Offset}: reported {table.RowCount} row(s) x {table.ColumnCount} column(s) — malformed." });
                else if (table.Cells.Count == 0)
                    issues.Add(new ValidationIssue { Stage = "TextQuality", Severity = "Warning",
                        DocumentId = blobName,
                        Message    = $"Table at offset {table.Offset}: {table.RowCount}x{table.ColumnCount} reported but no cell data was extracted." });
            }
        }

        return issues;
    }

    // 5. Total tables detected this run — real count from DI's table detection.
    private static int CountDetectedTables(IReadOnlyDictionary<string, PdfDocumentStructure>? structures) =>
        structures?.Values.Sum(s => s.Tables.Count) ?? 0;

    // 6. Document flagged if none of its pages has a markdown heading.
    private static List<string> DocsWithNoPagesWithHeading(PdfCleanResult cleanResult)
    {
        var docsWithHeadings = cleanResult.Records
            .Where(r => MarkdownHeading.IsMatch(r.PageContent))
            .Select(r => r.BlobName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return cleanResult.Records
            .Select(r => r.BlobName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(id => !docsWithHeadings.Contains(id))
            .ToList();
    }

    // 7. Random sample for human review.
    private static List<CleanedPdfPageRecord> BuildRandomCheckSample(PdfCleanResult cleanResult) =>
        cleanResult.Records.Count <= SpotCheckSampleSize
            ? [.. cleanResult.Records]
            : [.. cleanResult.Records.OrderBy(_ => Guid.NewGuid()).Take(SpotCheckSampleSize)];
}