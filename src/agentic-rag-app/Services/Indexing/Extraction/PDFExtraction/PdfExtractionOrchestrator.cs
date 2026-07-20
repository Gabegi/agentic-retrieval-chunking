using System.Collections.Concurrent;
using System.Security.Cryptography;
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
// shape (download -> extract -> clean -> validate -> report -> diff-ready output),
// adapted to the PDF pipeline. Not registered as the active IExtractionOrchestrator yet
// (CSV remains the sole active source — see program.cs); this exists so the PDF
// pipeline can run end-to-end, and so each file's PDFExtractionResult.Diagnostics
// (currently always null - nothing populates it since the PdfPig backend was removed)
// has somewhere to be written as a dev-only report. Extractors stay I/O-free;
// orchestrators own reporting — same split ExtractionService/CsvExtractionOrchestrator
// already follow for IRunReportWriter.
public class PdfExtractionOrchestrator : IExtractionOrchestrator
{
    private readonly BlobContainerClient                _container;
    private readonly BlobContainerClient                _stateContainer;
    private readonly IRunReportWriter                   _reportWriter;
    private readonly IPdfExtractor                      _extractor;
    private readonly IPdfCleaner                        _pdfCleaner;
    private readonly IPdfPipelineValidator               _validator;
    private readonly ILogger<PdfExtractionOrchestrator>  _logger;

    public string Source => "pdf";

    private const string StateBlobName = "pdf-extraction-state.json";

    // Folder segment namespacing every report blob this orchestrator writes, so it
    // doesn't mix into CsvExtractionOrchestrator's blobs in the same "telemetry-reports" container.
    private const string ReportFolder = "indexing/pdf-extraction";

    // See CsvExtractionOrchestrator.MaxLoggedIssues — same rationale (log volume/cost cap,
    // separate from MaxReturnedIssues below, which caps the *returned* issues list for
    // Durable's row-size limit).
    private const int MaxLoggedIssues = 100;

    // Cap on ExtractionOutput.Issues to stay safely under Durable Table Storage's 64KB
    // row-size limit — a different constraint from MaxLoggedIssues above, which just caps
    // log volume/cost. Coincidentally the same number today; not the same knob.
    private const int MaxReturnedIssues = 100;

    // Each blob triggers a paid, rate-limited Document Intelligence call; tune this
    // against DI's actual throttling limits before raising it.
    private const int MaxExtractionParallelism = 8;

    private sealed record RunState(int CleanedRecords);

    public PdfExtractionOrchestrator(
        BlobContainerClient                container,
        BlobContainerClient                stateContainer,
        IRunReportWriter                   reportWriter,
        IPdfExtractor                      extractor,
        IPdfCleaner                        cleaner,
        IPdfPipelineValidator              validator,
        ILogger<PdfExtractionOrchestrator> logger)
    {
        _container      = container;
        _stateContainer = stateContainer;
        _reportWriter   = reportWriter;
        _extractor      = extractor;
        _pdfCleaner        = cleaner;
        _validator      = validator;
        _logger         = logger;
    }

    public async Task<ExtractionOutput> ExtractDocumentsAsync(bool overrideMagnitudeCheck = false, CancellationToken ct = default)
    {
        var runAt = DateTimeOffset.UtcNow;

        // 1/ Extract Data from PDFs
        var (fileResults, lastModifiedByBlob) = await ExtractPdfsFromBlobAsync(ct);

        // Filters to successfully-extracted files and flattens their pages into one list
        // because _cleaner.Clean only needs page content — not the file-level error/warning data that _validator.Validate needs separately from fileResults itself
        var pages       = fileResults.Where(f => f.Ok).SelectMany(f => f.Pages!).ToList();
        var cleanResult = _pdfCleaner.CleanPdf(pages);

        // Check # of docs processed vs previous run
        var (previousCount, previousETag) = await PreviousRunCount(ct);
        var diagnostics = fileResults.Select(f => f.Diagnostics).OfType<PdfExtractionDiagnostics>().ToList();

        
        var report = _validator.Validate(fileResults, cleanResult, previousCount, diagnostics);

        await WriteReportsAsync(runAt, report, diagnostics, ct);

        var (effectivePassed, errors, warnings, missingTitle) =
            LogAndEmitValidationTelemetry(report, cleanResult, overrideMagnitudeCheck);

        if (!effectivePassed)
            throw new InvalidOperationException(
                $"PDF validation failed ({report.ReconciliationProblems.Count} reconciliation problem(s), " +
                $"{report.MagnitudeWarnings.Count} magnitude warning(s)) — aborting extraction.");

        // Whether passed normally or via override, this becomes the new baseline - an
        // override run resets the magnitude check so the NEXT run is auto-gated again
        // instead of comparing against the same stale count.
        await SaveRunStateAsync(cleanResult.Records.Count, previousETag, ct);

        return BuildExtractionOutput(report, cleanResult, errors, warnings, missingTitle, lastModifiedByBlob);
    }

    // Downloads and extracts every PDF blob in the container, up to MaxExtractionParallelism
    // at a time. One file's exception (network blip, an unexpected extractor bug) shouldn't
    // abort the whole run — it becomes a file-level ExtractionError instead, same treatment
    // TryOpenAndValidate already gives a corrupt PDF. Also captures each blob's storage
    // LastModified — that's what downstream diffing in ExtractionService needs to detect
    // new/updated/removed documents, not anything parsed out of the PDF's own text.
    private async Task<(List<PDFExtractionResult> Results, Dictionary<string, DateTimeOffset> LastModified)> ExtractPdfsFromBlobAsync(
        CancellationToken ct)
    {
        // Declares two thread-safe collections:
        // One to accumulate per-blob extraction results => ConcurrentBag<T> is a thread-safe, unordered collection, multiple threads can call .Add() on it at once without locking
        var results      = new ConcurrentBag<PDFExtractionResult>();
        var lastModified = new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);


        // Iterates through items in blob, for each BlobItem, runs the download-and-extract
        await Parallel.ForEachAsync(
            _container.GetBlobsAsync(cancellationToken: ct),
            new ParallelOptions { MaxDegreeOfParallelism = MaxExtractionParallelism, CancellationToken = ct },
            async (blobItem, cancellationToken) =>
            {
                
                // Skips any blob item whose name doesn't end in .pdf (case-insensitive), so non-PDF files in the container are ignored.
                if (!blobItem.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return;

                // we need a lastmodified date to tell new/updated docs apart from unchanged ones.
                // Recorded here, before the try block below, so failed downloads/extractions
                // still get an entry. Harmless today - a failed file produces no cleaned record,
                // so its entry here never reaches BuildExtractionOutput - but if this dictionary
                // is ever used for a pre-extraction skip decision, a failed file would look
                // "processed" and never get retried.
                if (blobItem.Properties.LastModified is { } modified)
                {
                    lastModified[blobItem.Name] = modified;
                }
                else
                {
                    _logger.LogWarning(
                        "'{Blob}' has no LastModified from blob storage — treating as never-modified so it isn't reprocessed every run.",
                        blobItem.Name);
                    lastModified[blobItem.Name] = DateTimeOffset.MinValue;
                }

                // Try block covers the download too: a failed download for one blob must not
                // abort the run - and under Parallel.ForEachAsync an uncaught exception would
                // also cancel the other in-flight tasks, discarding paid DI calls mid-flight.
                try
                {
                    // DownloadContentAsync is the docs-preferred API for blobs that fit in
                    // memory; avoids the MemoryStream + ToArray() double allocation.
                    var download = await _container.GetBlobClient(blobItem.Name).DownloadContentAsync(cancellationToken);
                    var pdfBytes = download.Value.Content.ToArray();

                    // Computed here, before any backend is invoked, so it applies regardless of
                    // which IPdfExtractor ends up running - same bytes always hash the same,
                    // independent of blob name, so a byte-identical re-upload is detectable before
                    // paying for extraction again. Not yet compared against anything (no store of
                    // previously-seen hashes exists) - logged for now so the value is at least
                    // visible while that dedup check gets built. Gated on IsEnabled so SHA-256 isn't
                    // computed on every blob, every run, when Debug logging is off (the normal case).
                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("'{Blob}' content hash: {Hash}", blobItem.Name, ComputeContentHash(pdfBytes));

                    results.Add(await _extractor.ExtractPDFAsync(blobItem.Name, pdfBytes, cancellationToken));
                }
                catch (OperationCanceledException)
                {
                    throw; // real cancellation should still stop the run, not log as a file error
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Download or extraction failed for '{Blob}'; recording as a file-level error.", blobItem.Name);
                    results.Add(new PDFExtractionResult(false, blobItem.Name, blobItem.Properties.ContentLength ?? 0, null, null, null, null, null, null,
                        new ExtractionError { DocumentId = blobItem.Name, Message = ex.Message, Reason = PdfOpenFailureReason.Unknown }));
                }
            });

        return (results.ToList(), new Dictionary<string, DateTimeOffset>(lastModified, StringComparer.OrdinalIgnoreCase));
    }

    // Dev-only (see IRunReportWriter.IsEnabled): the validation report (same shape
    // CsvExtractionOrchestrator writes) plus a second, PDF-only report of what each
    // extraction step actually produced per file. Currently always empty - diagnostics
    // is only ever populated by the PdfPig backend, which has been removed; left in place
    // as a report slot for whichever backend picks that reporting back up.
    private async Task WriteReportsAsync(
        DateTimeOffset runAt, PdfValidationReport report, List<PdfExtractionDiagnostics> diagnostics, CancellationToken ct)
    {
        if (!_reportWriter.IsEnabled) return;

        await _reportWriter.WriteReportAsync(
            $"{ReportFolder}/{runAt:yyyy/MM/dd}/{runAt:HHmmssfff}-validation-report.json", report, ct);

        if (diagnostics.Count > 0)
            await _reportWriter.WriteReportAsync(
                $"{ReportFolder}/{runAt:yyyy/MM/dd}/{runAt:HHmmssfff}-diagnostics.json", diagnostics, ct);
    }

    // Maps the validated, cleaned records into the source-agnostic ExtractionOutput
    // returned to the caller. Several ExtractionOutput fields have no PDF equivalent and
    // are always null here (not "verified zero") — see PdfValidationReport's own comment:
    // no Zenya attention-flag (StaleDocCount), no version data (MissingVersionCount -
    // nothing parses/populates Version for PDFs), and no folder/department concept
    // (MissingDepartmentCount).
    private static ExtractionOutput BuildExtractionOutput(
        PdfValidationReport                report,
        PdfCleanResult                      cleanResult,
        int                                 errors,
        int                                 warnings,
        int                                 missingTitle,
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
                    // Diff-relevant timestamp: the blob's own storage LastModified.
                    ["last_modified_date"] = lastModifiedByBlob.TryGetValue(r.BlobName, out var lm)
                        ? lm.ToString("yyyy-MM-dd")
                        : "",
                }))
            .ToList();

        var issues = report.Issues
            .Take(MaxReturnedIssues)
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
            StaleDocCount:          null,  // no Zenya attention-flag equivalent for PDFs
            MojibakeRepairedPages:  report.MojibakeRepairedPages,
            DetectedTableCount:     report.DetectedTableCount,
            DocsWithoutHeadings:    report.DocumentsNeedingFallbackChunking.Count,
            MissingTitleCount:      missingTitle,
            MissingVersionCount:    null,  // no version data for PDFs
            MissingDepartmentCount: null,  // no folder/department concept for PDFs
            Issues:                 issues,
            RedFlags:               report.RedFlags.ToList(),
            SpotCheckSample:        spotCheck);
    }

    // Everything this run logs and emits as metrics, in one place — mirrors
    // CsvExtractionOrchestrator.LogAndEmitValidationTelemetry, minus the metrics
    // (StaleDocs, "department"/"version" metadata) that have no PDF equivalent.
    private (bool EffectivePassed, int Errors, int Warnings, int MissingTitle)
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
        var byDocument   = cleanResult.Records.GroupBy(r => r.BlobName).ToList();
        var missingTitle = byDocument.Count(g => g.All(r => string.IsNullOrWhiteSpace(r.Title)));

        Instrumentation.MissingMetadata.Add(missingTitle, sourceTag, new("field", "title"));

        return (effectivePassed, errors, warnings, missingTitle);
    }

    // Stable hash of the PDF's raw bytes, used as a dedup/caching key:
    // - Same file content -> same hash, regardless of the blob's file name.
    // - Would let a future caller detect "this exact file was already processed" and skip
    //   paying for another extraction call - not wired into a skip decision yet, since
    //   there's nowhere that stores previously-seen hashes across runs.
    private static string ComputeContentHash(byte[] pdfBytes) =>
        Convert.ToHexString(SHA256.HashData(pdfBytes));

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
