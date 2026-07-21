namespace AgenticRag.Observability.Reports;

public interface IRunReportWriter
{
    bool IsEnabled { get; }

    // Callers build the blob path themselves (e.g. "indexing/{yyyy}/{MM}/{dd}/{id}.json") and
    // supply whatever serializable report/data they need written - one method instead of a
    // dedicated wrapper per report type. Gated by IsEnabled: callers should check it first.
    Task WriteReportAsync<T>(string path, T report, CancellationToken ct = default);

    // Baseline for drift checks. Unlike the methods above, these are NOT gated by IsEnabled —
    // they must work in every environment, since drift detection is pointless if it only runs in dev.
    // Returns null if no baseline exists yet (first run) or the read fails.
    Task<(long DocumentCount, long StorageSizeBytes)?> GetLastIndexStatsAsync(CancellationToken ct = default);
    Task SaveLastIndexStatsAsync(long documentCount, long storageSizeBytes, CancellationToken ct = default);
}
