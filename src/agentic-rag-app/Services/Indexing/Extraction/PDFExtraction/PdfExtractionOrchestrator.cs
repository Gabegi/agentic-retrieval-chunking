using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Models;
using ProtocolsIndexer.Observability;
using ProtocolsIndexer.Observability.Reports;

namespace ProtocolsIndexer.Services;

// PDF implementation of IExtractionOrchestrator — mirrors CsvExtractionOrchestrator's
// shape (download -> extract -> join -> clean -> validate -> report -> diff-ready
// output), adapted to the PDF pipeline. Not registered as the active
// IExtractionOrchestrator yet (CSV remains the sole active source — see program.cs);
// this exists so the PDF pipeline can run end-to-end, and so each file's
// PdfFileExtraction.Diagnostics (baseline/decoration/metadata — see PdfPigExtractor)
// has somewhere to be written as a dev-only report. Extractors stay I/O-free;
// orchestrators own reporting — same split ExtractionService/CsvExtractionOrchestrator
// already follow for IRunReportWriter.
public class PdfExtractionOrchestrator : IExtractionOrchestrator
{
    private readonly BlobContainerClient                _container;
    private readonly BlobContainerClient                _stateContainer;
    private readonly IRunReportWriter                   _reportWriter;
    private readonly IPdfExtractor                      _extractor;
    private readonly IPdfJoiner                         _joiner;
    private readonly IPdfCleaner                        _cleaner;
    private readonly IPdfPipelineValidator               _validator;
    private readonly PdfBackendComparisonRunner          _comparisonRunner;
    private readonly ILogger<PdfExtractionOrchestrator>  _logger;

    public string Source => "pdf";

    private const string StateBlobName = "pdf-extraction-state.json";

    // Folder segment namespacing every report blob this orchestrator writes, so it
    // doesn't mix into CsvExtractionOrchestrator's blobs in the same "telemetry-reports" container.
    private const string ReportFolder = "indexing/pdf-extraction";

    // See CsvExtractionOrchestrator.MaxLoggedIssues — same rationale (log volume/cost cap,
    // separate from the Take(100) on the *returned* issues list for Durable's row-size limit).
    private const int MaxLoggedIssues = 100;

    private sealed record RunState(int CleanedRecords);

    public PdfExtractionOrchestrator(
        BlobContainerClient                container,
        BlobContainerClient                stateContainer,
        IRunReportWriter                   reportWriter,
        IPdfExtractor                      extractor,
        IPdfJoiner                         joiner,
        IPdfCleaner                        cleaner,
        IPdfPipelineValidator              validator,
        PdfBackendComparisonRunner         comparisonRunner,
        ILogger<PdfExtractionOrchestrator> logger)
    {
        _container        = container;
        _stateContainer    = stateContainer;
        _reportWriter      = reportWriter;
        _extractor         = extractor;
        _joiner             = joiner;
        _cleaner            = cleaner;
        _validator          = validator;
        _comparisonRunner   = comparisonRunner;
        _logger             = logger;
    }

    public async Task<ExtractionOutput> ExtractDocumentsAsync(bool overrideMagnitudeCheck = false, CancellationToken ct = default)
    {
        var runAt = DateTimeOffset.UtcNow;

        var (fileResults, lastModifiedByBlob) = await ExtractAllFilesAsync(ct);

        var (pagesResult, indexResult) = PdfExtractionAggregation.Aggregate(fileResults);
        var joinResult  = _joiner.Join(pagesResult.Records, indexResult.Records);
        var cleanResult = _cleaner.Clean(joinResult.Joined);

        var (previousCount, previousETag) = await PreviousRunCount(ct);
        var report = _validator.Validate(pagesResult, indexResult, joinResult, cleanResult, previousCount);

        await WriteReportsAsync(runAt, report, fileResults, ct);

        var (effectivePassed, errors, warnings, missingTitle, missingVersion) =
            LogAndEmitValidationTelemetry(report, cleanResult, overrideMagnitudeCheck);

        if (!effectivePassed)
            throw new InvalidOperationException(
                $"PDF validation failed ({report.ReconciliationProblems.Count} reconciliation problem(s), " +
                $"{report.MagnitudeWarnings.Count} magnitude warning(s)) — aborting extraction.");

        // Whether passed normally or via override, this becomes the new baseline - an
        // override run resets the magnitude check so the NEXT run is auto-gated again
        // instead of comparing against the same stale count.
        await SaveRunStateAsync(cleanResult.Records.Count, previousETag, ct);

        return BuildExtractionOutput(report, cleanResult, errors, warnings, missingTitle, missingVersion, lastModifiedByBlob);
    }

    // Downloads and extracts every PDF blob in the container. One file's exception
    // (network blip, an unexpected extractor bug) shouldn't abort the whole run — it
    // becomes a file-level ExtractionError instead, same treatment TryOpenAndValidate
    // already gives a corrupt PDF. Also captures each blob's storage LastModified —
    // that's what downstream diffing in ExtractionService needs to detect new/updated/
    // removed documents, not anything parsed out of the PDF's own text.
    private async Task<(List<PdfFileExtraction> Results, Dictionary<string, DateTimeOffset> LastModified)> ExtractAllFilesAsync(
        CancellationToken ct)
    {
        var results      = new List<PdfFileExtraction>();
        var lastModified  = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        await foreach (var item in _container.GetBlobsAsync(cancellationToken: ct))
        {
            if (!item.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) continue;

            lastModified[item.Name] = item.Properties.LastModified ?? DateTimeOffset.UtcNow;

            using var ms = new MemoryStream();
            await _container.GetBlobClient(item.Name).DownloadToAsync(ms, ct);

            try
            {
                results.Add(_extractor.ExtractPDF(item.Name, ms.ToArray()));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Extractor threw for '{Blob}'; recording as a file-level error.", item.Name);
                results.Add(new PdfFileExtraction([], null,
                    new ExtractionError { DocumentId = item.Name, Message = ex.Message, Reason = PdfOpenFailureReason.Unknown }));
            }
        }

        return (results, lastModified);
    }

    // Dev-only (see IRunReportWriter.IsEnabled): the validation report (same shape
    // CsvExtractionOrchestrator writes) plus a second, PDF-only report of what each
    // extraction step actually produced per file. PdfPigExtractor always populates
    // PdfFileExtraction.Diagnostics — it's cheap to build; only writing it out per run
    // is the part worth gating.
    private async Task WriteReportsAsync(
        DateTimeOffset runAt, PdfValidationReport report, List<PdfFileExtraction> fileResults, CancellationToken ct)
    {
        if (!_reportWriter.IsEnabled) return;

        await _reportWriter.WriteReportAsync(
            $"{ReportFolder}/{runAt:yyyy/MM/dd}/{runAt:HHmmssfff}-validation-report.json", report, ct);

        var diagnostics = fileResults.Select(f => f.Diagnostics).Where(d => d != null).ToList();
        if (diagnostics.Count > 0)
            await _reportWriter.WriteReportAsync(
                $"{ReportFolder}/{runAt:yyyy/MM/dd}/{runAt:HHmmssfff}-diagnostics.json", diagnostics, ct);
    }

    // Maps the validated, cleaned records into the source-agnostic ExtractionOutput
    // returned to the caller. Two ExtractionOutput fields have no PDF equivalent and
    // are always zero here — see PdfValidationReport's own comment: no Zenya
    // attention-flag (StaleDocCount) and no folder/department concept for PDFs
    // (MissingDepartmentCount).
    private static ExtractionOutput BuildExtractionOutput(
        PdfValidationReport                report,
        PdfCleanResult                      cleanResult,
        int                                 errors,
        int                                 warnings,
        int                                 missingTitle,
        int                                 missingVersion,
        Dictionary<string, DateTimeOffset>  lastModifiedByBlob)
    {
        var extractionDocs = cleanResult.Records
            .Select(r => new ExtractionDocument(
                SourceId: r.BlobName,
                Ordinal:  r.PageIndex,
                Content:  r.PageContent,
                Metadata: new Dictionary<string, string>
                {
                    ["title"]              = r.Title,
                    ["version"]            = r.Version,
                    // Diff-relevant timestamp: the blob's own storage LastModified, not
                    // the PDF's self-reported publication date (kept separately below).
                    ["last_modified_date"] = lastModifiedByBlob.TryGetValue(r.BlobName, out var lm)
                        ? lm.ToString("yyyy-MM-dd")
                        : "",
                    ["publication_date"]   = r.PublicationDate?.ToString("o") ?? "",
                }))
            .ToList();

        var issues = report.Issues
            .Take(100)  // cap to stay safely under Durable Table Storage 64KB limit
            .Select(i => new ValidationIssueEntry(i.Stage, i.Severity, i.DocumentId, i.Message))
            .ToList();

        var spotCheck = report.SpotCheckSample
            .Select(r => new SpotCheckEntry(
                r.BlobName,
                r.Title,
                r.PageContent.Length > 300 ? r.PageContent[..300] + "…" : r.PageContent))
            .ToList();

        return new ExtractionOutput(
            Docs:                   extractionDocs,
            ValidationErrors:       errors,
            ValidationWarnings:     warnings,
            ReconciliationProblems: report.ReconciliationProblems.Count,
            StaleDocCount:          0,  // no Zenya attention-flag equivalent for PDFs
            MojibakeRepairedPages:  report.MojibakeRepairedPages,
            DetectedTableCount:     report.DetectedTableCount,
            DocsWithoutHeadings:    report.DocumentsNeedingFallbackChunking.Count,
            MissingTitleCount:      missingTitle,
            MissingVersionCount:    missingVersion,
            MissingDepartmentCount: 0,  // no folder/department concept for PDFs
            Issues:                 issues,
            RedFlags:               report.RedFlags.ToList(),
            SpotCheckSample:        spotCheck);
    }

    // Everything this run logs and emits as metrics, in one place — mirrors
    // CsvExtractionOrchestrator.LogAndEmitValidationTelemetry, minus the two metrics
    // (StaleDocs, "department" metadata) that have no PDF equivalent.
    private (bool EffectivePassed, int Errors, int Warnings, int MissingTitle, int MissingVersion)
        LogAndEmitValidationTelemetry(
            PdfValidationReport report,
            PdfCleanResult      cleanResult,
            bool                overrideMagnitudeCheck)
    {
        var magnitudeOverrideApplied = !report.Passed && overrideMagnitudeCheck && report.PassedExcludingMagnitude;
        var effectivePassed          = report.Passed || magnitudeOverrideApplied;

        foreach (var warning in report.MagnitudeWarnings)
            _logger.LogWarning("{Warning}", warning);

        if (magnitudeOverrideApplied)
            _logger.LogWarning(
                "VALIDATION OVERRIDE APPLIED — magnitude-shift gate bypassed by explicit operator request. " +
                "{Cleaned} records this run. Warnings: {Warnings}",
                cleanResult.Records.Count, string.Join(" | ", report.MagnitudeWarnings));

        _logger.LogInformation("PDF validation {Result} — {Cleaned} records, {Issues} issues",
            effectivePassed ? "passed" : "failed", report.CleanedRecords, report.Issues.Count);

        foreach (var issue in report.Issues.Take(MaxLoggedIssues))
            _logger.Log(
                issue.Severity == "Error" ? LogLevel.Error : LogLevel.Warning,
                "[{Stage}] {DocId}: {Message}", issue.Stage, issue.DocumentId, issue.Message);
        if (report.Issues.Count > MaxLoggedIssues)
            _logger.LogWarning("…{More} more issue(s) not logged (see the run report for the full list).",
                report.Issues.Count - MaxLoggedIssues);

        var errors   = report.Issues.Count(i => i.Severity == "Error");
        var warnings = report.Issues.Count(i => i.Severity != "Error");

        var sourceTag = new KeyValuePair<string, object?>("source", Source);

        Instrumentation.ValidationIssues.Add(errors,   sourceTag, new("severity", "error"));
        Instrumentation.ValidationIssues.Add(warnings, sourceTag, new("severity", "warning"));
        Instrumentation.DocsWithoutHeadings.Add(report.DocumentsNeedingFallbackChunking.Count, sourceTag);
        Instrumentation.MojibakeRepairedPages.Add(report.MojibakeRepairedPages, sourceTag);
        Instrumentation.DetectedTableCount.Record(report.DetectedTableCount, sourceTag);

        // Metadata completeness — a document only counts as "missing" if EVERY one of
        // its pages lacks that field, matching CsvExtractionOrchestrator's rule.
        var byDocument     = cleanResult.Records.GroupBy(r => r.BlobName).ToList();
        var missingTitle   = byDocument.Count(g => g.All(r => string.IsNullOrWhiteSpace(r.Title)));
        var missingVersion = byDocument.Count(g => g.All(r => string.IsNullOrWhiteSpace(r.Version)));

        Instrumentation.MissingMetadata.Add(missingTitle,   sourceTag, new("field", "title"));
        Instrumentation.MissingMetadata.Add(missingVersion, sourceTag, new("field", "version"));

        return (effectivePassed, errors, warnings, missingTitle, missingVersion);
    }

    private async Task<(int? Count, ETag? ETag)> PreviousRunCount(CancellationToken ct)
    {
        var blob = _stateContainer.GetBlobClient(StateBlobName);
        if (!await blob.ExistsAsync(ct)) return (null, null);

        try
        {
            var download = await blob.DownloadContentAsync(ct);
            var state    = download.Value.Content.ToObjectFromJson<RunState>();
            return (state?.CleanedRecords, download.Value.Details.ETag);
        }
        catch (JsonException ex)
        {
            // A corrupt/partially-written state blob shouldn't brick the whole run -
            // it just means "no usable baseline", the same as the blob not existing.
            _logger.LogWarning(ex,
                "State blob '{Blob}' contains invalid JSON — treating as no previous baseline.", StateBlobName);
            return (null, null);
        }
    }

    private async Task SaveRunStateAsync(int cleanedRecords, ETag? previousETag, CancellationToken ct)
    {
        var json       = JsonSerializer.Serialize(new RunState(cleanedRecords));
        var conditions = previousETag is ETag tag
            ? new BlobRequestConditions { IfMatch = tag }
            : new BlobRequestConditions { IfNoneMatch = ETag.All };

        try
        {
            await _stateContainer.GetBlobClient(StateBlobName)
                .UploadAsync(BinaryData.FromString(json), new BlobUploadOptions { Conditions = conditions }, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 412)
        {
            // Lost a race with another concurrent run's save - not worth failing this
            // otherwise-successful run over. The next run's magnitude check just
            // compares against whichever baseline won the race.
            _logger.LogWarning(
                "State blob '{Blob}' was updated concurrently — this run's baseline was not saved.", StateBlobName);
        }
    }
}
