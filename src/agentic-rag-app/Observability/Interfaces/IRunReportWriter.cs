using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Observability.Reports;

public interface IRunReportWriter
{
    bool IsEnabled { get; }
    Task WriteQueryReportAsync(QueryRunReport report, CancellationToken ct = default);
    Task WriteIndexReportAsync(IndexRunReport report, CancellationToken ct = default);

    // Full (uncapped) join classification for a single extraction run - NotFound/Inactive/
    // Duplicate/SkippedIndexRecords. Written directly to blob so it never has to pass through
    // the Durable Table Storage-backed activity return that forces Issues to be capped at 100
    // (see CsvExtractionOrchestrator). Gated by IsEnabled, same as WriteIndexReportAsync.
    Task WriteJoinIssuesAsync(IReadOnlyList<ValidationIssue> issues, DateTimeOffset runAt, CancellationToken ct = default);

    // Baseline for drift checks. Unlike the methods above, these are NOT gated by IsEnabled —
    // they must work in every environment, since drift detection is pointless if it only runs in dev.
    // Returns null if no baseline exists yet (first run) or the read fails.
    Task<(long DocumentCount, long StorageSizeBytes)?> GetLastIndexStatsAsync(CancellationToken ct = default);
    Task SaveLastIndexStatsAsync(long documentCount, long storageSizeBytes, CancellationToken ct = default);
}
