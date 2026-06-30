namespace ProtocolsIndexer.Observability.Reports;

public sealed class NullRunReportWriter : IRunReportWriter
{
    public Task WriteQueryReportAsync(QueryRunReport report, CancellationToken ct = default) => Task.CompletedTask;
    public Task WriteIndexReportAsync(IndexRunReport report, CancellationToken ct = default) => Task.CompletedTask;
}
