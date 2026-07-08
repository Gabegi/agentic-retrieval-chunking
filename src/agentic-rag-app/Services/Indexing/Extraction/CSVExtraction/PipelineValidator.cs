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
        var reconciliation = new List<string>();
        var magnitude     = new List<string>();
        var redFlags      = new List<string>();

        // 1. Collect all errors from all 3 previous steps
        var issues = CollectIssues(pagesExtraction, indexExtraction, joinResult, cleanResult, redFlags);

        // 2. Reconcile counts across step boundaries.
        reconciliation.AddRange(ReconcileCounts(pagesExtraction, joinResult, cleanResult));

        // 3. Magnitude shift vs a previous run, if supplied.
        magnitude.AddRange(CheckMagnitudeShift(cleanResult, previousRunCleanedCount));

        // 4. Domain-specific red flag: documents flagged as overdue for review.
        var staleDocCount = CountStaleDocs(cleanResult);
        if (staleDocCount > 0)
            redFlags.Add($"{staleDocCount} document(s) flagged check_date_exceeded — guidance may be outdated.");

        // 5. Text-quality signals on Zenya's source text.
        issues.AddRange(CollectTextQualityIssues(cleanResult));

        // 6. Structure presence per document — directly informs Chunking's strategy choice.
        var docsNeedingFallback = FindDocsNeedingFallbackChunking(cleanResult);
        if (docsNeedingFallback.Count > 0)
            redFlags.Add($"{docsNeedingFallback.Count} document(s) have no markdown headings — need fallback chunking.");

        // 7. Spot-check sample for human review.
        var sample = BuildSpotCheckSample(cleanResult);

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

    // 2. Reconcile counts across step boundaries.
    private static List<string> ReconcileCounts(
        ExtractionResult<PageRecord> pagesExtraction,
        JoinResult                   joinResult,
        CleanResult                  cleanResult)
    {
        var reconciliation = new List<string>();

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

        // Zero cleaned records is never a legitimate outcome, even an export where
        // every document happens to be inactive. Unlike the magnitude-shift check below,
        // this fires with no previous-run baseline required — a first-ever run (or a run
        // right after a lost/corrupt state blob) shouldn't be able to sail through empty.
        // Folded into reconciliation (not magnitude) so overrideMagnitudeCheck can never
        // bypass it — see the magnitude-shift comment below on why an empty run is
        // dangerous downstream (the diff step deletes anything "missing").
        if (cleanResult.Records.Count == 0)
            reconciliation.Add("Zero cleaned records produced — refusing to pass an empty run.");

        return reconciliation;
    }

    // 1. Aggregate every error/warning bucket into one place.
    private static List<ValidationIssue> CollectIssues(
        ExtractionResult<PageRecord>  pagesExtraction,
        ExtractionResult<IndexRecord> indexExtraction,
        JoinResult                    joinResult,
        CleanResult                   cleanResult,
        List<string>                  redFlags)
    {
        var issues = new List<ValidationIssue>();

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

        return issues;
    }
}
