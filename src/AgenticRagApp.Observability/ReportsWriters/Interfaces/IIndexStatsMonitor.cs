namespace AgenticRagApp.Observability.Reports;

// Records whole-index size telemetry (Instrumentation histograms, every environment —
// not gated by IRunReportWriter.IsEnabled, since drift dashboards need data everywhere)
// and flags a run-over-run doc-count swing beyond a threshold versus the last saved
// baseline for this source, then saves these stats as the new baseline. Source-scoped
// (IRunReportWriter.GetLastIndexStatsAsync/SaveLastIndexStatsAsync) so PDF and CSV never
// compare against each other's baseline. One shared instance — each doc-type's own
// UploadService calls this after Infrastructure's IIndexDocumentService.GetStatisticsAsync,
// instead of owning its own copy of this comparison logic.
public interface IIndexStatsMonitor
{
    Task<IReadOnlyList<string>> RecordAndCheckDriftAsync(
        string source, long documentCount, long storageSizeBytes, CancellationToken ct = default);
}
