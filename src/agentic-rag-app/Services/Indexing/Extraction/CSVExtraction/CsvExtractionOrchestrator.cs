using Azure.Storage.Blobs;
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

    public async Task<ExtractionOutput> ExtractDocumentsAsync(CancellationToken ct = default)
    {
        using var pagesStream = new MemoryStream();
        using var indexStream = new MemoryStream();

        await Task.WhenAll(
            _container.GetBlobClient(PagesBlobName).DownloadToAsync(pagesStream, ct),
            _container.GetBlobClient(IndexBlobName).DownloadToAsync(indexStream, ct));

        pagesStream.Position = 0;
        indexStream.Position = 0;

        var pagesResult = CsvExtractor.ExtractPages(pagesStream);
        var indexResult = CsvExtractor.ExtractIndex(indexStream);
        var joinResult  = CsvJoiner.Join(pagesResult.Records, indexResult.Records);
        var cleanResult = DataCleaner.Clean(joinResult.Joined);

        var previousCount = await ReadPreviousCleanedCountAsync(ct);
        var report = PipelineValidator.Validate(pagesResult, indexResult, joinResult, cleanResult, previousCount);

        foreach (var warning in report.MagnitudeWarnings)
            _logger.LogWarning("{Warning}", warning);

        _logger.LogInformation("CSV validation {Result} — {Cleaned} records, {Issues} issues",
            report.Passed ? "passed" : "failed", report.CleanedRecords, report.Issues.Count);

        foreach (var issue in report.Issues)
            _logger.Log(
                issue.Severity == "Error" ? LogLevel.Error : LogLevel.Warning,
                "[{Stage}] {DocId}: {Message}", issue.Stage, issue.DocumentId, issue.Message);

        if (!report.Passed)
            throw new InvalidOperationException(
                $"CSV validation failed ({report.ReconciliationProblems.Count} reconciliation problem(s)) — aborting extraction.");

        await SaveRunStateAsync(cleanResult.Records.Count, ct);   // only after a passing run

        // Emit validation metrics
        var errors   = report.Issues.Count(i => i.Severity == "Error");
        var warnings = report.Issues.Count(i => i.Severity != "Error");

        var sourceTag = new KeyValuePair<string, object?>("source", Source);

        if (errors > 0)
            Instrumentation.ValidationIssues.Add(errors, sourceTag, new("severity", "error"));
        if (warnings > 0)
            Instrumentation.ValidationIssues.Add(warnings, sourceTag, new("severity", "warning"));

        if (report.RedFlags.Count > 0)
        {
            // Extract stale doc count from the red-flag message produced by PipelineValidator
            var staleFlag = report.RedFlags.FirstOrDefault(f => f.Contains("check_date_exceeded"));
            if (staleFlag != null && int.TryParse(
                    new string(staleFlag.TakeWhile(char.IsDigit).ToArray()), out var staleCount))
                Instrumentation.StaleDocs.Add(staleCount, sourceTag);
        }

        Instrumentation.DocsWithoutHeadings.Add(report.DocumentsNeedingFallbackChunking.Count, sourceTag);

        // Metadata completeness — count docs missing title, version, department
        var docs = cleanResult.Records.ToList();
        var missingTitle      = docs.DistinctBy(r => r.DocumentId).Count(r => string.IsNullOrWhiteSpace(r.Title));
        var missingVersion    = docs.DistinctBy(r => r.DocumentId).Count(r => string.IsNullOrWhiteSpace(r.Version));
        var missingDepartment = docs.DistinctBy(r => r.DocumentId).Count(r => string.IsNullOrWhiteSpace(r.FolderPath));

        if (missingTitle > 0)      Instrumentation.MissingMetadata.Add(missingTitle,      sourceTag, new("field", "title"));
        if (missingVersion > 0)    Instrumentation.MissingMetadata.Add(missingVersion,    sourceTag, new("field", "version"));
        if (missingDepartment > 0) Instrumentation.MissingMetadata.Add(missingDepartment, sourceTag, new("field", "department"));

        var staleDocCount = cleanResult.Records
            .Where(r => r.AttentionFlags.Contains("check_date_exceeded"))
            .Select(r => r.DocumentId)
            .Distinct()
            .Count();

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
            StaleDocCount:          staleDocCount,
            DocsWithoutHeadings:    report.DocumentsNeedingFallbackChunking.Count,
            MissingTitleCount:      missingTitle,
            MissingVersionCount:    missingVersion,
            MissingDepartmentCount: missingDepartment,
            Issues:                 issues,
            RedFlags:               report.RedFlags.ToList(),
            SpotCheckSample:        spotCheck);
    }
}
