using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// CSV implementation of IExtractionOrchestrator.
// Downloads pages.csv + index.csv from blob storage, runs the full CSV pipeline,
// and returns source-agnostic ExtractionDocuments. Callers see no CSV internals.
public class CsvExtractionOrchestrator : IExtractionOrchestrator
{
    private readonly BlobContainerClient                _container;
    private readonly ILogger<CsvExtractionOrchestrator> _logger;

    public string Source => "csv";

    private const string PagesBlobName = "zenya_pages.csv";
    private const string IndexBlobName = "zenya_index.csv";

    public CsvExtractionOrchestrator(
        BlobContainerClient                container,
        ILogger<CsvExtractionOrchestrator> logger)
    {
        _container = container;
        _logger    = logger;
    }

    public async Task<IReadOnlyList<ExtractionDocument>> ExtractAsync(CancellationToken ct = default)
    {
        using var pagesStream = new MemoryStream();
        using var indexStream = new MemoryStream();

        await _container.GetBlobClient(PagesBlobName).DownloadToAsync(pagesStream, ct);
        await _container.GetBlobClient(IndexBlobName).DownloadToAsync(indexStream, ct);

        pagesStream.Position = 0;
        indexStream.Position = 0;

        var pagesResult = CsvExtractor.ExtractPages(pagesStream);
        var indexResult = CsvExtractor.ExtractIndex(indexStream);
        var joinResult  = CsvJoiner.Join(pagesResult.Records, indexResult.Records);
        var cleanResult = DataCleaner.Clean(joinResult.Joined);
        var report      = PipelineValidator.Validate(pagesResult, indexResult, joinResult, cleanResult);

        _logger.LogInformation("CSV validation {Result} — {Cleaned} records, {Issues} issues",
            report.Passed ? "passed" : "failed", report.CleanedRecords, report.Issues.Count);

        foreach (var issue in report.Issues)
            _logger.Log(
                issue.Severity == "Error" ? LogLevel.Error : LogLevel.Warning,
                "[{Stage}] {DocId}: {Message}", issue.Stage, issue.DocumentId, issue.Message);

        if (!report.Passed)
            throw new InvalidOperationException(
                $"CSV validation failed ({report.ReconciliationProblems.Count} reconciliation problem(s)) — aborting extraction.");

        return cleanResult.Records
            .Select(r => new ExtractionDocument(
                SourceId: r.DocumentId,
                Ordinal:  r.PageIndex,
                Content:  r.PageContent,
                Metadata: new Dictionary<string, string>
                {
                    ["title"]            = r.Title,
                    ["version"]          = r.Version,
                    ["publication_date"] = r.LastModified.ToString("yyyy-MM-dd"),
                    ["quick_code"]       = r.QuickCode,
                    ["folder_path"]      = r.FolderPath,
                    ["document_type"]    = r.DocumentTypeName,
                    ["summary"]          = r.Summary,
                }))
            .ToList();
    }
}
