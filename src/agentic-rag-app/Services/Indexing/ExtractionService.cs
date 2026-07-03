using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Models;
using ProtocolsIndexer.Observability;
using ProtocolsIndexer.Observability.Reports;

namespace ProtocolsIndexer.Services;

public class ExtractionService : IExtractionService
{
    private readonly IReadOnlyList<IExtractionOrchestrator> _extractors;
    private readonly IIndexDocumentService                  _indexDocumentService;
    private readonly ILogger<ExtractionService>             _logger;

    public ExtractionService(
        IEnumerable<IExtractionOrchestrator> extractors,
        IIndexDocumentService                indexDocumentService,
        ILogger<ExtractionService>           logger)
    {
        _extractors           = extractors.ToList();
        _indexDocumentService = indexDocumentService;
        _logger               = logger;
    }

    public async Task<(IReadOnlyList<ExtractionDocument> Docs, ExtractionResults Stats)> ExtractAsync(
        string source, bool forceReindex, CancellationToken ct = default)
    {
        var extractor = _extractors.FirstOrDefault(e => string.Equals(e.Source, source, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"No extraction orchestrator registered for source '{source}'. Known sources: {string.Join(", ", _extractors.Select(e => e.Source))}");

        var diff = await ExtractAndDiffAsync(extractor, forceReindex, ct);
        EmitMetrics(diff);
        return (diff.ToProcess, BuildStats(diff));
    }

    // 1. Call the source extractor, diff results against the current index state
    private async Task<DiffResult> ExtractAndDiffAsync(IExtractionOrchestrator extractor, bool forceReindex, CancellationToken ct)
    {
        var output       = await extractor.ExtractDocumentsAsync(ct);
        var indexedDates = await _indexDocumentService.GetIndexedDocumentDatesAsync(ct);

        var toProcess      = new List<ExtractionDocument>();
        var toDeleteChunks = new List<string>();
        var seenSourceIds  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newCount       = 0;
        var updated        = 0;

        foreach (var doc in output.Docs)
        {
            seenSourceIds.Add(doc.SourceId);

            if (!indexedDates.TryGetValue(doc.SourceId, out var indexedDate))
            {
                toProcess.Add(doc);
                newCount++;
                continue;
            }

            if (!forceReindex)
            {
                var modifiedStr = doc.Metadata.GetValueOrDefault("last_modified_date");
                if (DateTimeOffset.TryParse(modifiedStr, out var modifiedDate) && modifiedDate <= indexedDate)
                    continue;
            }

            toDeleteChunks.Add(doc.SourceId);
            toProcess.Add(doc);
            updated++;
        }

        // Docs that were previously indexed but no longer appear in the source — withdrawn/removed
        // upstream. Their chunks must be deleted, not just skipped, or they keep getting retrieved.
        var removedSourceIds = indexedDates.Keys.Where(id => !seenSourceIds.Contains(id)).ToList();
        toDeleteChunks.AddRange(removedSourceIds);

        var skipped = output.Docs.Count - toProcess.Count;
        var chunksRemoved = 0;

        if (toDeleteChunks.Count > 0)
        {
            _logger.LogInformation(
                "Deleting stale chunks for {Updated} updated and {Removed} removed documents",
                updated, removedSourceIds.Count);
            chunksRemoved = await _indexDocumentService.DeleteDocumentsAsync(toDeleteChunks, ct);
        }

        _logger.LogInformation(
            "Extraction diff — source '{Source}': {New} new, {Updated} updated, {Removed} removed, {Skipped} skipped",
            extractor.Source, newCount, updated, removedSourceIds.Count, skipped);

        return new DiffResult(output, toProcess, removedSourceIds, newCount, updated, skipped, chunksRemoved);
    }

    // 2. Emit instrumentation metrics from the diff result
    private static void EmitMetrics(DiffResult diff)
    {
        Instrumentation.DocsExtracted.Add(diff.Output.Docs.Count);
        Instrumentation.DocsSkipped.Add(diff.Skipped);
        Instrumentation.DocsNew.Add(diff.NewCount);
        Instrumentation.DocsUpdated.Add(diff.Updated);
        Instrumentation.DocsDeleted.Add(diff.RemovedSourceIds.Count);
        Instrumentation.ChunksRemoved.Add(diff.ChunksRemoved);
    }

    // 3. Assemble ExtractionResults to return to the activity
    private static ExtractionResults BuildStats(DiffResult diff) => new(
        DocsToProcess:          diff.ToProcess.Count,
        DocsSkipped:            diff.Skipped,
        DocsNew:                diff.NewCount,
        DocsUpdated:            diff.Updated,
        DocsDeleted:            diff.RemovedSourceIds.Count,
        ChunksRemoved:          diff.ChunksRemoved,
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
        List<string>             RemovedSourceIds,
        int                      NewCount,
        int                      Updated,
        int                      Skipped,
        int                      ChunksRemoved);
}
