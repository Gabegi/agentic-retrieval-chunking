using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public static class PipelineValidator
{
    private const double MaxAcceptableErrorRatePercent     = 1.0;
    private const double MaxAcceptableMagnitudeShiftPercent = 20.0;
    private const int    SpotCheckSampleSize               = 5;

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

        // 1. Aggregate every error/warning bucket into one place
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

        // 2. Reconcile counts across step boundaries.
        // Join dedupes its error log per DOCUMENT, not per page, so recompute the real
        // per-page total for unmatched docs before comparing.
        var unmatchedDocIds    = joinResult.Errors.Select(e => e.DocumentId).ToHashSet();
        var unmatchedPageCount = pagesExtraction.Records.Count(p => unmatchedDocIds.Contains(p.DocumentId));
        if (joinResult.Joined.Count + unmatchedPageCount != pagesExtraction.Records.Count)
        {
            reconciliation.Add(
                $"Parse->Join mismatch: {pagesExtraction.Records.Count} pages extracted, but " +
                $"{joinResult.Joined.Count} joined + {unmatchedPageCount} on unmatched docs = " +
                $"{joinResult.Joined.Count + unmatchedPageCount}.");
        }

        // Join -> Clean: every joined record is processed exactly once.
        if (cleanResult.Records.Count + cleanResult.Errors.Count != joinResult.Joined.Count)
        {
            reconciliation.Add(
                $"Join->Clean mismatch: {joinResult.Joined.Count} joined, but " +
                $"{cleanResult.Records.Count} cleaned + {cleanResult.Errors.Count} errored = " +
                $"{cleanResult.Records.Count + cleanResult.Errors.Count}.");
        }

        // 3. Magnitude shift vs a previous run, if one is supplied.
        if (previousRunCleanedCount is int previous && previous > 0)
        {
            var deltaPercent = 100.0 * (cleanResult.Records.Count - previous) / previous;
            if (Math.Abs(deltaPercent) > MaxAcceptableMagnitudeShiftPercent)
            {
                magnitude.Add(
                    $"Cleaned count shifted {deltaPercent:+0.0;-0.0}% vs previous run " +
                    $"({previous} -> {cleanResult.Records.Count}) — exceeds {MaxAcceptableMagnitudeShiftPercent}% threshold.");
            }
        }

        // 4. Referential integrity: no duplicate (DocumentId, PageIndex) in the final output.
        var duplicateKeys = cleanResult.Records
            .GroupBy(r => (r.DocumentId, r.PageIndex))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.DocumentId} / page {g.Key.PageIndex} appears {g.Count()} times");
        reconciliation.AddRange(duplicateKeys.Select(d => $"Duplicate output key: {d}"));

        // 5. Domain-specific red flag: documents flagged as overdue for review.
        var staleDocCount = cleanResult.Records
            .Where(r => r.AttentionFlags.Contains("check_date_exceeded"))
            .Select(r => r.DocumentId)
            .Distinct()
            .Count();
        if (staleDocCount > 0)
            redFlags.Add($"{staleDocCount} document(s) flagged check_date_exceeded — guidance may be outdated.");

        // 6. Spot-check sample — surfaced for a human to read, not auto-validated.
        List<CleanedPageRecord> sample = cleanResult.Records.Count <= SpotCheckSampleSize
            ? [.. cleanResult.Records]
            : [.. cleanResult.Records.OrderBy(_ => Guid.NewGuid()).Take(SpotCheckSampleSize)];

        // 7. Pass/fail: totalAttempted as denominator so zero-records forces failure.
        var errorCount    = issues.Count(i => i.Severity == "Error") + reconciliation.Count;
        var totalAttempted = pagesExtraction.TotalRows;
        var errorRate     = totalAttempted == 0 ? 100.0
            : 100.0 * errorCount / totalAttempted;
        var passed = errorRate <= MaxAcceptableErrorRatePercent && reconciliation.Count == 0;

        return new ValidationReport
        {
            RunAtUtc              = DateTime.UtcNow,
            PagesExtracted        = pagesExtraction.Records.Count,
            IndexRecordsExtracted = indexExtraction.Records.Count,
            JoinedRecords         = joinResult.Joined.Count,
            CleanedRecords        = cleanResult.Records.Count,
            Issues                = issues,
            ReconciliationProblems = reconciliation,
            MagnitudeWarnings     = magnitude,
            RedFlags              = redFlags,
            SpotCheckSample       = sample,
            Passed                = passed,
        };
    }
}
