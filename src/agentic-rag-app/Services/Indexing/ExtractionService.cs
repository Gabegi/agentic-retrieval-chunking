using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Models;
using ProtocolsIndexer.Observability;
using ProtocolsIndexer.Observability.Reports;

namespace ProtocolsIndexer.Services;

public class ExtractionService : IExtractionService
{
    // Exactly one extractor is active at a time - see the registration comment in program.cs.
    // Switching source (e.g. to a future PDF extractor) means replacing that registration, not
    // adding branching here.
    private readonly IExtractionOrchestrator    _extractor;
    private readonly IIndexDocumentService      _indexDocumentService;
    private readonly ILogger<ExtractionService> _logger;

    public ExtractionService(
        IExtractionOrchestrator    extractor,
        IIndexDocumentService      indexDocumentService,
        ILogger<ExtractionService> logger)
    {
        _extractor             = extractor;
        _indexDocumentService  = indexDocumentService;
        _logger                = logger;
    }

    public async Task<(IReadOnlyList<ExtractionDocument> Docs, ExtractionResults Stats)> ExtractAsync(
        bool forceReindex, CancellationToken ct = default)
    {
        var diff = await ExtractAndDiffAsync(forceReindex, ct);
        EmitMetrics(diff);
        return (diff.ToProcess, BuildStats(diff));
    }

    // 1. Call the source extractor, diff results against the current index state
    private async Task<DiffResult> ExtractAndDiffAsync(bool forceReindex, CancellationToken ct)
    {
        var output       = await _extractor.ExtractDocumentsAsync(ct);
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
        // upstream. Their chunks are stale the same way an updated doc's old chunks are - not
        // deleted here, just flagged. Deletion happens after the replacement content (for updated
        // docs) is safely re-embedded and re-uploaded, so a failure later in the pipeline never
        // leaves a document with no chunks at all - see UploadService.UploadDocumentsAsync.
        var removedSourceIds = indexedDates.Keys.Where(id => !seenSourceIds.Contains(id)).ToList();
        toDeleteChunks.AddRange(removedSourceIds);

        var skipped = output.Docs.Count - toProcess.Count;

        _logger.LogInformation(
            "Extraction diff — source '{Source}': {New} new, {Updated} updated, {Removed} removed, {Skipped} skipped",
            _extractor.Source, newCount, updated, removedSourceIds.Count, skipped);

        return new DiffResult(_extractor.Source, output, toProcess, removedSourceIds, toDeleteChunks, newCount, updated, skipped);
    }

    // 2. Emit instrumentation metrics from the diff result
    private static void EmitMetrics(DiffResult diff)
    {
        Instrumentation.DocsExtracted.Add(diff.Output.Docs.Count);
        Instrumentation.DocsSkipped.Add(diff.Skipped);
        Instrumentation.DocsNew.Add(diff.NewCount);
        Instrumentation.DocsUpdated.Add(diff.Updated);
        Instrumentation.DocsDeleted.Add(diff.RemovedSourceIds.Count);
    }

    // 3. Assemble ExtractionResults to return to the activity
    private static ExtractionResults BuildStats(DiffResult diff) => new(
        Source:                 diff.Source,
        DocsToProcess:          diff.ToProcess.Count,
        DocsSkipped:            diff.Skipped,
        DocsNew:                diff.NewCount,
        DocsUpdated:            diff.Updated,
        DocsDeleted:            diff.RemovedSourceIds.Count,
        StaleDocumentIds:       diff.StaleDocumentIds,
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
        string                   Source,
        ExtractionOutput         Output,
        List<ExtractionDocument> ToProcess,
        List<string>             RemovedSourceIds,
        List<string>             StaleDocumentIds,
        int                      NewCount,
        int                      Updated,
        int                      Skipped);
}
