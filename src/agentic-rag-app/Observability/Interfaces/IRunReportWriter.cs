namespace ProtocolsIndexer.Observability.Reports;

public interface IRunReportWriter
{
    bool IsEnabled { get; }
    Task WriteQueryReportAsync(QueryRunReport report, CancellationToken ct = default);
    Task WriteIndexReportAsync(IndexRunReport report, CancellationToken ct = default);

    // Baseline for drift checks. Unlike the methods above, these are NOT gated by IsEnabled —
    // they must work in every environment, since drift detection is pointless if it only runs in dev.
    // Returns null if no baseline exists yet (first run) or the read fails.
    Task<(long DocumentCount, long StorageSizeBytes)?> GetLastIndexStatsAsync(CancellationToken ct = default);
    Task SaveLastIndexStatsAsync(long documentCount, long storageSizeBytes, CancellationToken ct = default);
}
