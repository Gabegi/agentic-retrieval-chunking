using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Hosting;

namespace ProtocolsIndexer.Observability.Reports;

public class RunReportWriter : IRunReportWriter
{
    private static readonly JsonSerializerOptions s_opts = new() { WriteIndented = true };
    private readonly BlobContainerClient _container;
    private readonly bool _enabled;

    public RunReportWriter(BlobContainerClient container, IHostEnvironment env)
    {
        _container = container;
        _enabled   = env.IsDevelopment();
    }

    public Task WriteQueryReportAsync(QueryRunReport report, CancellationToken ct = default) =>
        _enabled ? WriteAsync($"queries/{report.Timestamp:yyyy/MM/dd}/{report.RunId}.json", report, ct) : Task.CompletedTask;

    public Task WriteIndexReportAsync(IndexRunReport report, CancellationToken ct = default) =>
        _enabled ? WriteAsync($"indexing/{report.StartedAt:yyyy/MM/dd}/{report.InstanceId}.json", report, ct) : Task.CompletedTask;

    private async Task WriteAsync<T>(string path, T data, CancellationToken ct)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: ct);
        var json = JsonSerializer.SerializeToUtf8Bytes(data, s_opts);
        using var ms = new MemoryStream(json);
        await _container.GetBlobClient(path).UploadAsync(ms, overwrite: true, cancellationToken: ct);
    }
}
