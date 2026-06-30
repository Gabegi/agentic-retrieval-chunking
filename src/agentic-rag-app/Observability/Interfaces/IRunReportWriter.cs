namespace ProtocolsIndexer.Observability.Reports;

public interface IRunReportWriter
{
    Task WriteQueryReportAsync(QueryRunReport report, CancellationToken ct = default);
    Task WriteIndexReportAsync(IndexRunReport report, CancellationToken ct = default);
}
