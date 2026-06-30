namespace ProtocolsIndexer.Observability.Reports;

public interface IRunReportWriter
{
    bool IsEnabled { get; }
    Task WriteQueryReportAsync(QueryRunReport report, CancellationToken ct = default);
    Task WriteIndexReportAsync(IndexRunReport report, CancellationToken ct = default);
}
