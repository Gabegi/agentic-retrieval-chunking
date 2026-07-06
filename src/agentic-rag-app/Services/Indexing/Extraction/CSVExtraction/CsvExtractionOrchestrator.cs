using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Models;
using ProtocolsIndexer.Observability;
using System.Text.Json;

namespace ProtocolsIndexer.Services;

// CSV implementation of IExtractionOrchestrator.
// Downloads pages.csv + index.csv from blob storage, runs the full CSV pipeline,
// and returns source-agnostic ExtractionDocuments plus quality metadata from the validator.
public class CsvExtractionOrchestrator : IExtractionOrchestrator
{
    private readonly BlobContainerClient                _container;
    private readonly BlobContainerClient                _stateContainer;
    private readonly ILogger<CsvExtractionOrchestrator> _logger;

    public string Source => "csv";

    private const string PagesBlobName = "zenya_pages.csv";
    private const string IndexBlobName = "zenya_index.csv";
    private const string StateBlobName = "csv-extraction-state.json";

    // Caps how many individual validation issues get their own log line. A badly
    // malformed file can produce thousands of near-identical issues - real log
    // volume/cost at that point, not useful signal. This is separate from the
    // Take(100) further down on the *returned* issues list, which exists for a
    // different reason (Durable Table Storage's 64KB row-size limit).
    private const int MaxLoggedIssues = 100;

    private sealed record RunState(int CleanedRecords);

    public CsvExtractionOrchestrator(
        BlobContainerClient                container,
        BlobContainerClient                stateContainer,
        ILogger<CsvExtractionOrchestrator> logger)
    {
        _container      = container;
        _stateContainer = stateContainer;
        _logger         = logger;
    }

    // Persisted in the pipeline-internal "pipeline-temp" container, not the CSV drop
    // container — the CSV container is overwritten by an external Zenya export process
    // outside this repo, and it's unclear whether that's a scoped overwrite of the two
    // named CSVs or a wholesale container wipe. Using our own container avoids the state
    // file silently disappearing before it's ever read back.
    private async Task<int?> ReadPreviousCleanedCountAsync(CancellationToken ct)
    {
        var blob = _stateContainer.GetBlobClient(StateBlobName);
        if (!await blob.ExistsAsync(ct)) return null;
        using var stream = await blob.OpenReadAsync(cancellationToken: ct);
        var state = await JsonSerializer.DeserializeAsync<RunState>(stream, cancellationToken: ct);
        return state?.CleanedRecords;
    }

    private async Task SaveRunStateAsync(int cleanedRecords, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(new RunState(cleanedRecords));
        await _stateContainer.GetBlobClient(StateBlobName)
            .UploadAsync(BinaryData.FromString(json), overwrite: true, ct);
    }

    public async Task<ExtractionOutput> ExtractDocumentsAsync(bool overrideMagnitudeCheck = false, CancellationToken ct = default)
    {
        // Stream directly from blob storage instead of buffering the whole file into a
        // MemoryStream first - CsvExtractor only ever reads forward through the stream
        // once, so there's nothing gained by holding the entire file in memory before
        // parsing starts, and a large export would otherwise mean two full files
        // resident in memory at once for no reason.
        var pagesStreamTask = _container.GetBlobClient(PagesBlobName).OpenReadAsync(cancellationToken: ct);
        var indexStreamTask = _container.GetBlobClient(IndexBlobName).OpenReadAsync(cancellationToken: ct);
        await Task.WhenAll(pagesStreamTask, indexStreamTask);

        await using var pagesStream = await pagesStreamTask;
        await using var indexStream = await indexStreamTask;

        var pagesResult = CsvExtractor.ExtractPages(pagesStream);
        var indexResult = CsvExtractor.ExtractIndex(indexStream);
        var joinResult  = CsvJoiner.Join(pagesResult.Records, indexResult.Records);
        var cleanResult = DataCleaner.Clean(joinResult.Joined);

        var previousCount = await ReadPreviousCleanedCountAsync(ct);
        var report = PipelineValidator.Validate(pagesResult, indexResult, joinResult, cleanResult, previousCount);

        foreach (var warning in report.MagnitudeWarnings)
            _logger.LogWarning("{Warning}", warning);

        // A magnitude-only failure can be deliberately let through by the caller; error-rate
        // and reconciliation failures never are, regardless of overrideMagnitudeCheck.
        var magnitudeOverrideApplied = !report.Passed && overrideMagnitudeCheck && report.PassedExcludingMagnitude;
        var effectivePassed          = report.Passed || magnitudeOverrideApplied;

        if (magnitudeOverrideApplied)
            _logger.LogWarning(
                "VALIDATION OVERRIDE APPLIED — magnitude-shift gate bypassed by explicit operator request. " +
                "{Cleaned} records this run. Warnings: {Warnings}",
                cleanResult.Records.Count, string.Join(" | ", report.MagnitudeWarnings));

        _logger.LogInformation("CSV validation {Result} — {Cleaned} records, {Issues} issues",
            effectivePassed ? "passed" : "failed", report.CleanedRecords, report.Issues.Count);

        foreach (var issue in report.Issues.Take(MaxLoggedIssues))
            _logger.Log(
                issue.Severity == "Error" ? LogLevel.Error : LogLevel.Warning,
                "[{Stage}] {DocId}: {Message}", issue.Stage, issue.DocumentId, issue.Message);
        if (report.Issues.Count > MaxLoggedIssues)
            _logger.LogWarning("…{More} more issue(s) not logged (see the run report for the full list).",
                report.Issues.Count - MaxLoggedIssues);

        // Emit validation metrics before the pass/fail gate below - a failed run is
        // exactly the case these metrics matter most for, and a throw would otherwise
        // skip every one of them.
        var errors   = report.Issues.Count(i => i.Severity == "Error");
        var warnings = report.Issues.Count(i => i.Severity != "Error");

        var sourceTag = new KeyValuePair<string, object?>("source", Source);

        // Unconditional, not guarded with "> 0": these are counters, and a healthy run
        // (zero errors/stale docs) should still emit a zero data point. Otherwise a
        // dashboard/alert can't tell "this metric is healthy at zero" apart from "this
        // metric stopped reporting entirely".
        Instrumentation.ValidationIssues.Add(errors,   sourceTag, new("severity", "error"));
        Instrumentation.ValidationIssues.Add(warnings, sourceTag, new("severity", "warning"));
        Instrumentation.StaleDocs.Add(report.StaleDocCount, sourceTag);
        Instrumentation.DocsWithoutHeadings.Add(report.DocumentsNeedingFallbackChunking.Count, sourceTag);

        // Metadata completeness — count docs missing title, version, department. Title
        // and FolderPath are page-CSV fields and can legitimately vary page-to-page for
        // the same document, so a document only counts as "missing" if EVERY one of its
        // pages lacks that field, not just whichever page happens to come first.
        var docs = cleanResult.Records.ToList();
        var missingTitle      = docs.GroupBy(r => r.DocumentId).Count(g => g.All(r => string.IsNullOrWhiteSpace(r.Title)));
        var missingVersion    = docs.GroupBy(r => r.DocumentId).Count(g => g.All(r => string.IsNullOrWhiteSpace(r.Version)));
        var missingDepartment = docs.GroupBy(r => r.DocumentId).Count(g => g.All(r => string.IsNullOrWhiteSpace(r.FolderPath)));

        Instrumentation.MissingMetadata.Add(missingTitle,      sourceTag, new("field", "title"));
        Instrumentation.MissingMetadata.Add(missingVersion,    sourceTag, new("field", "version"));
        Instrumentation.MissingMetadata.Add(missingDepartment, sourceTag, new("field", "department"));

        if (!effectivePassed)
            throw new InvalidOperationException(
                $"CSV validation failed ({report.ReconciliationProblems.Count} reconciliation problem(s), " +
                $"{report.MagnitudeWarnings.Count} magnitude warning(s)) — aborting extraction.");

        // Whether passed normally or via override, this becomes the new baseline - an
        // override run resets the magnitude check so the NEXT run is auto-gated again
        // instead of comparing against the same stale count.
        await SaveRunStateAsync(cleanResult.Records.Count, ct);

        var extractionDocs = cleanResult.Records
            .Select(r => new ExtractionDocument(
                SourceId: r.DocumentId,
                Ordinal:  r.PageIndex,
                Content:  r.PageContent,
                Metadata: new Dictionary<string, string>
                {
                    ["title"]              = r.Title,
                    ["version"]            = r.Version,
                    ["last_modified_date"] = r.LastModified.ToString("yyyy-MM-dd"),
                    ["check_date"]         = r.CheckDate?.ToString("o") ?? "",
                    ["quick_code"]         = r.QuickCode,
                    ["folder_path"]        = r.FolderPath,
                    ["document_type"]      = r.DocumentTypeName,
                    ["summary"]            = r.Summary,
                    ["relative_path"]      = r.RelativePath,
                }))
            .ToList();

        var issues = report.Issues
            .Take(100)  // cap to stay safely under Durable Table Storage 64KB limit
            .Select(i => new ValidationIssueEntry(i.Stage, i.Severity, i.DocumentId, i.Message))
            .ToList();

        var spotCheck = report.SpotCheckSample
            .Select(r => new SpotCheckEntry(
                r.DocumentId,
                r.Title,
                r.PageContent.Length > 300 ? r.PageContent[..300] + "…" : r.PageContent))
            .ToList();

        return new ExtractionOutput(
            Docs:                   extractionDocs,
            ValidationErrors:       errors,
            ValidationWarnings:     warnings,
            ReconciliationProblems: report.ReconciliationProblems.Count,
            StaleDocCount:          report.StaleDocCount,
            DocsWithoutHeadings:    report.DocumentsNeedingFallbackChunking.Count,
            MissingTitleCount:      missingTitle,
            MissingVersionCount:    missingVersion,
            MissingDepartmentCount: missingDepartment,
            Issues:                 issues,
            RedFlags:               report.RedFlags.ToList(),
            SpotCheckSample:        spotCheck);
    }
}
