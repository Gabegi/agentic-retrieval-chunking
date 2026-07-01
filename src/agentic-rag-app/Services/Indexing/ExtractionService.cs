using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Models;
using ProtocolsIndexer.Observability;
using ProtocolsIndexer.Observability.Reports;

namespace ProtocolsIndexer.Services;

public class ExtractionService : IExtractionService
{
    private readonly IExtractionOrchestrator    _extractor;
    private readonly IIndexDocumentService      _indexDocumentService;
    private readonly ILogger<ExtractionService> _logger;

    public ExtractionService(
        IExtractionOrchestrator    extractor,
        IIndexDocumentService      indexDocumentService,
        ILogger<ExtractionService> logger)
    {
        _extractor            = extractor;
        _indexDocumentService = indexDocumentService;
        _logger               = logger;
    }

    public async Task<(IReadOnlyList<ExtractionDocument> Docs, ExtractionResults Stats)> ExtractAsync(
        CancellationToken ct = default)
    {
        var diff = await ExtractAndDiffAsync(ct);
        EmitMetrics(diff);
        return (diff.ToProcess, BuildStats(diff));
    }

    // 1. Call the source extractor, diff results against the current index state
    private async Task<DiffResult> ExtractAndDiffAsync(CancellationToken ct)
    {
        var output       = await _extractor.ExtractDocumentsAsync(ct);
        var indexedDates = await _indexDocumentService.GetIndexedDocumentDatesAsync(ct);

        var toProcess = new List<ExtractionDocument>();
        var toDelete  = new List<string>();
        var newCount  = 0;
        var updated   = 0;

        foreach (var doc in output.Docs)
        {
            if (!indexedDates.TryGetValue(doc.SourceId, out var indexedDate))
            {
                toProcess.Add(doc);
                newCount++;
                continue;
            }

            var modifiedStr = doc.Metadata.GetValueOrDefault("last_modified_date");
            if (DateTimeOffset.TryParse(modifiedStr, out var modifiedDate) && modifiedDate <= indexedDate)
                continue;

            toDelete.Add(doc.SourceId);
            toProcess.Add(doc);
            updated++;
        }

        var skipped = output.Docs.Count - toProcess.Count;

        if (toDelete.Count > 0)
        {
            _logger.LogInformation("Deleting stale chunks for {Count} updated documents", toDelete.Count);
            await _indexDocumentService.DeleteDocumentsAsync(toDelete, ct);
        }

        _logger.LogInformation(
            "Extraction diff — source '{Source}': {New} new, {Updated} updated, {Skipped} skipped",
            _extractor.Source, newCount, updated, skipped);

        return new DiffResult(output, toProcess, toDelete, newCount, updated, skipped);
    }

    // 2. Emit instrumentation metrics from the diff result
    private static void EmitMetrics(DiffResult diff)
    {
        Instrumentation.DocsExtracted.Add(diff.Output.Docs.Count);
        Instrumentation.DocsSkipped.Add(diff.Skipped);
        Instrumentation.DocsNew.Add(diff.NewCount);
        Instrumentation.DocsUpdated.Add(diff.Updated);
        Instrumentation.DocsDeleted.Add(diff.ToDelete.Count);
        Instrumentation.ValidationIssues.Add(diff.Output.ValidationErrors + diff.Output.ValidationWarnings);
        Instrumentation.StaleDocs.Add(diff.Output.StaleDocCount);
        Instrumentation.DocsWithoutHeadings.Add(diff.Output.DocsWithoutHeadings);
    }

    // 3. Assemble ExtractionResults to return to the activity
    private static ExtractionResults BuildStats(DiffResult diff) => new(
        DocsToProcess:          diff.ToProcess.Count,
        DocsSkipped:            diff.Skipped,
        DocsNew:                diff.NewCount,
        DocsUpdated:            diff.Updated,
        DocsDeleted:            diff.ToDelete.Count,
        ValidationErrors:       diff.Output.ValidationErrors,
        ValidationWarnings:     diff.Output.ValidationWarnings,
        ReconciliationProblems: diff.Output.ReconciliationProblems,
        StaleDocCount:          diff.Output.StaleDocCount,
        DocsWithoutHeadings:    diff.Output.DocsWithoutHeadings,
        MissingTitleCount:      diff.Output.MissingTitleCount,
        MissingVersionCount:    diff.Output.MissingVersionCount,
        MissingDepartmentCount: diff.Output.MissingDepartmentCount,
        Issues:                 diff.Output.Issues,
        RedFlags:               diff.Output.RedFlags,
        SpotCheckSample:        diff.Output.SpotCheckSample);

    private record DiffResult(
        ExtractionOutput         Output,
        List<ExtractionDocument> ToProcess,
        List<string>             ToDelete,
        int                      NewCount,
        int                      Updated,
        int                      Skipped);
}
