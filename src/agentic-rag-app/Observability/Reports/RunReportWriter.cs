using System.Text.Json;
using Azure.Storage.Blobs;

namespace ProtocolsIndexer.Observability.Reports;

public class RunReportWriter : IRunReportWriter
{
    private static readonly JsonSerializerOptions s_opts = new() { WriteIndented = true };
    private readonly BlobContainerClient _container;

    public RunReportWriter(BlobContainerClient container) => _container = container;

    public Task WriteQueryReportAsync(QueryRunReport report, CancellationToken ct = default) =>
        WriteAsync($"queries/{report.Timestamp:yyyy/MM/dd}/{report.RunId}.json", report, ct);

    public Task WriteIndexReportAsync(IndexRunReport report, CancellationToken ct = default) =>
        WriteAsync($"indexing/{report.StartedAt:yyyy/MM/dd}/{report.InstanceId}.json", report, ct);

    private async Task WriteAsync<T>(string path, T data, CancellationToken ct)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: ct);
        var json = JsonSerializer.SerializeToUtf8Bytes(data, s_opts);
        using var ms = new MemoryStream(json);
        await _container.GetBlobClient(path).UploadAsync(ms, overwrite: true, cancellationToken: ct);
    }
}
