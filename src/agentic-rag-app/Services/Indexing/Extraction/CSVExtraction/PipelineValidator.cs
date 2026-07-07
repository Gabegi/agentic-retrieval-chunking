using System.Text.RegularExpressions;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public static class PipelineValidator
{
    private const double MaxAcceptableErrorRatePercent      = 1.0;
    private const double MaxAcceptableMagnitudeShiftPercent = 20.0;
    private const int    SpotCheckSampleSize                = 5;
    private const char   ReplacementChar                    = '�';

    private static readonly Regex MarkdownHeading =
        new(@"^#{1,6}\s", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex MarkdownTableLine =
        new(@"^\s*\|.*\|\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    public static ValidationReport Validate(
        ExtractionResult<PageRecord>  pagesExtraction,
        ExtractionResult<IndexRecord> indexExtraction,
        JoinResult                    joinResult,
        CleanResult                   cleanResult,
        int?                          previousRunCleanedCount = null)
    {
        var issues        = new List<ValidationIssue>();
        var reconciliation = new List<string>();
        var magnitude     = new List<string>();
        var redFlags      = new List<string>();

        // 1. Aggregate every error/warning bucket into one place.
        issues.AddRange(pagesExtraction.Errors.Select(e => new ValidationIssue
            { Stage = "Parse:Pages", Severity = "Error", DocumentId = e.DocumentId ?? "", Message = $"Row {e.RowNumber}: {e.Message}" }));
        issues.AddRange(indexExtraction.Errors.Select(e => new ValidationIssue
            { Stage = "Parse:Index", Severity = "Error", DocumentId = e.DocumentId ?? "", Message = $"Row {e.RowNumber}: {e.Message}" }));
        issues.AddRange(joinResult.Errors.Select(e => new ValidationIssue
            { Stage = "Join", Severity = "Error", DocumentId = e.DocumentId, Message = e.Message }));
        issues.AddRange(joinResult.DataQualityWarnings.Select(w => new ValidationIssue
            { Stage = "Join", Severity = "Warning", DocumentId = w.DocumentId, Message = w.Message }));
        issues.AddRange(cleanResult.Errors.Select(e => new ValidationIssue
            { Stage = "Clean", Severity = "Error", DocumentId = e.DocumentId, Message = e.Message }));
        issues.AddRange(cleanResult.Warnings.Select(w => new ValidationIssue
            { Stage = "Clean", Severity = "Warning", DocumentId = w.DocumentId, Message = w.Message }));

        // 1b. Index documents with no pages never reach the search index — make that visible.
        issues.AddRange(joinResult.SkippedIndexRecords.Select(r => new ValidationIssue
        {
            Stage      = "Join",
            Severity   = "Warning",
            DocumentId = r.DocumentId,
            Message    = "Index record has no pages — document will not be indexed.",
        }));
        if (joinResult.SkippedIndexRecords.Count > 0)
            redFlags.Add($"{joinResult.SkippedIndexRecords.Count} index document(s) have no pages and will not be indexed.");

        // 2. Reconcile counts across step boundaries.
        // Join dedupes its error log per DOCUMENT, not per page — recompute the real
        // per-page total for unmatched docs before comparing.
        var unmatchedDocIds    = joinResult.Errors.Select(e => e.DocumentId).ToHashSet();
        var unmatchedPageCount = pagesExtraction.Records.Count(p => unmatchedDocIds.Contains(p.DocumentId));
        if (joinResult.Joined.Count + unmatchedPageCount + joinResult.InactivePagesSkipped
                != pagesExtraction.Records.Count)
            reconciliation.Add(
                $"Parse->Join mismatch: {pagesExtraction.Records.Count} pages extracted, but " +
                $"{joinResult.Joined.Count} joined + {unmatchedPageCount} unmatched + " +
                $"{joinResult.InactivePagesSkipped} inactive-skipped.");

        // Join -> Clean: every joined record is processed exactly once.
        if (cleanResult.Records.Count + cleanResult.Errors.Count + cleanResult.DuplicatePagesSkipped
                != joinResult.Joined.Count)
            reconciliation.Add(
                $"Join->Clean mismatch: {joinResult.Joined.Count} joined, but " +
                $"{cleanResult.Records.Count} cleaned + {cleanResult.Errors.Count} errored + " +
                $"{cleanResult.DuplicatePagesSkipped} duplicate-skipped.");

        // 2b. Zero cleaned records is never a legitimate outcome, even an export where
        // every document happens to be inactive. Unlike the magnitude-shift check below,
        // this fires with no previous-run baseline required — a first-ever run (or a run
        // right after a lost/corrupt state blob) shouldn't be able to sail through empty.
        // Folded into reconciliation (not magnitude) so overrideMagnitudeCheck can never
        // bypass it — see the magnitude-shift comment below on why an empty run is
        // dangerous downstream (the diff step deletes anything "missing").
        if (cleanResult.Records.Count == 0)
            reconciliation.Add("Zero cleaned records produced — refusing to pass an empty run.");

        // 3. Magnitude shift vs a previous run, if supplied.
        if (previousRunCleanedCount is int previous && previous > 0)
        {
            var deltaPercent = 100.0 * (cleanResult.Records.Count - previous) / previous;
            if (Math.Abs(deltaPercent) > MaxAcceptableMagnitudeShiftPercent)
                magnitude.Add(
                    $"Cleaned count shifted {deltaPercent:+0.0;-0.0}% vs previous run " +
                    $"({previous} -> {cleanResult.Records.Count}) — exceeds {MaxAcceptableMagnitudeShiftPercent}% threshold.");
        }

        // 4. Referential integrity: no duplicate (DocumentId, PageIndex) in the final output.
        // Defense-in-depth, not reachable today — DataCleaner.Clean already dedupes on
        // this exact key before a record ever reaches cleanResult.Records, so this can
        // only fire if that upstream guarantee is ever broken. Kept as a cheap invariant
        // check rather than relied on as live logic.
        var duplicateKeys = cleanResult.Records
            .GroupBy(r => (r.DocumentId, r.PageIndex))
            .Where(g => g.Count() > 1)
            .Select(g => $"Duplicate output key: {g.Key.DocumentId} / page {g.Key.PageIndex} appears {g.Count()} times");
        reconciliation.AddRange(duplicateKeys);

        // 5. Domain-specific red flag: documents flagged as overdue for review.
        var staleDocCount = cleanResult.Records
            .Where(r => r.AttentionFlags.Contains("check_date_exceeded"))
            .Select(r => r.DocumentId)
            .Distinct()
            .Count();
        if (staleDocCount > 0)
            redFlags.Add($"{staleDocCount} document(s) flagged check_date_exceeded — guidance may be outdated.");

        // 6. Text-quality signals on Zenya's source text.
        foreach (var record in cleanResult.Records)
        {
            var replacementCount = record.PageContent.Count(c => c == ReplacementChar);
            if (replacementCount > 0)
                issues.Add(new ValidationIssue { Stage = "TextQuality", Severity = "Error",
                    DocumentId = record.DocumentId,
                    Message    = $"Page {record.PageIndex}: {replacementCount} U+FFFD char(s) — source text is corrupted." });

            foreach (var (pattern, fix) in KnownMojibakePatterns)
            {
                if (record.PageContent.Contains(pattern))
                {
                    issues.Add(new ValidationIssue { Stage = "TextQuality", Severity = "Warning",
                        DocumentId = record.DocumentId,
                        Message    = $"Page {record.PageIndex}: possible mojibake '{pattern}' (expected '{fix}')." });
                    break;   // one mojibake warning per page is enough
                }
            }

            if (!string.IsNullOrEmpty(record.Language) &&
                !record.Language.StartsWith("nl", StringComparison.OrdinalIgnoreCase))
                issues.Add(new ValidationIssue { Stage = "TextQuality", Severity = "Warning",
                    DocumentId = record.DocumentId,
                    Message    = $"Page {record.PageIndex}: language '{record.Language}' — nl.microsoft analyzer will tokenize this poorly." });

            // Check each table block independently — a page can legitimately contain
            // multiple tables of different widths; checking the whole page at once
            // would flag that as "inconsistent" when both tables are individually fine.
            var tableBlocks = record.PageContent
                .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                .Where(block => MarkdownTableLine.IsMatch(block));

            foreach (var block in tableBlocks)
            {
                var pipeCounts = MarkdownTableLine.Matches(block)
                    .Select(m => m.Value.Count(ch => ch == '|'))
                    .ToList();
                if (pipeCounts.Count > 1 && pipeCounts.Distinct().Count() > 1)
                {
                    issues.Add(new ValidationIssue { Stage = "TextQuality", Severity = "Warning",
                        DocumentId = record.DocumentId,
                        Message    = $"Page {record.PageIndex}: markdown table has inconsistent column counts across rows." });
                    break; // one warning per page is enough
                }
            }
        }

        // 7. Structure presence per document — directly informs Chunking's strategy choice.
        var docsWithHeadings = cleanResult.Records
            .Where(r => MarkdownHeading.IsMatch(r.PageContent))
            .Select(r => r.DocumentId)
            .ToHashSet();
        var docsNeedingFallback = cleanResult.Records
            .Select(r => r.DocumentId)
            .Distinct()
            .Where(id => !docsWithHeadings.Contains(id))
            .ToList();
        if (docsNeedingFallback.Count > 0)
            redFlags.Add($"{docsNeedingFallback.Count} document(s) have no markdown headings — need fallback chunking.");

        // 8. Spot-check sample for human review.
        List<CleanedPageRecord> sample = cleanResult.Records.Count <= SpotCheckSampleSize
            ? [.. cleanResult.Records]
            : [.. cleanResult.Records.OrderBy(_ => Guid.NewGuid()).Take(SpotCheckSampleSize)];

        // 9. Pass/fail. Denominator is every row attempted across both inputs — parse errors
        // from the index file count against the same budget they're measured by.
        // reconciliation.Count is deliberately NOT folded in here: a reconciliation problem
        // is a pipeline-integrity assertion, not a per-row issue, so mixing it into a
        // rows-denominated rate would compare different units. It's already an
        // unconditional hard gate via reconciliation.Count == 0 below, regardless of rate.
        var errorCount     = issues.Count(i => i.Severity == "Error");
        var totalAttempted = pagesExtraction.RowsAttempted + indexExtraction.RowsAttempted;
        var errorRate      = totalAttempted == 0 ? 100.0 : 100.0 * errorCount / totalAttempted;
        var passedExcludingMagnitude = errorRate <= MaxAcceptableErrorRatePercent && reconciliation.Count == 0;
        // Magnitude shift is a hard gate too - a truncated export (rows silently dropped
        // upstream) can look perfectly well-formed row-by-row, and the diff step deletes
        // any previously-indexed document that's "missing" from a bad run. Only an
        // explicit operator override should be able to proceed past this specific check.
        var passed        = passedExcludingMagnitude && magnitude.Count == 0;

        return new ValidationReport
        {
            RunAtUtc                      = DateTime.UtcNow,
            PagesExtracted                = pagesExtraction.Records.Count,
            IndexRecordsExtracted         = indexExtraction.Records.Count,
            JoinedRecords                 = joinResult.Joined.Count,
            CleanedRecords                = cleanResult.Records.Count,
            Issues                        = issues,
            ReconciliationProblems        = reconciliation,
            MagnitudeWarnings             = magnitude,
            RedFlags                      = redFlags,
            SpotCheckSample               = sample,
            DocumentsNeedingFallbackChunking = docsNeedingFallback,
            SkippedIndexDocuments         = joinResult.SkippedIndexRecords
                .Select(r => $"{r.DocumentTypeName} ({r.DocumentId})")
                .ToList(),
            StaleDocCount                 = staleDocCount,
            Passed                        = passed,
            PassedExcludingMagnitude      = passedExcludingMagnitude,
        };
    }
}
