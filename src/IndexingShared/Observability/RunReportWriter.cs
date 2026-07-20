using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Hosting;

namespace IndexingShared.Observability.Reports;

public class RunReportWriter : IRunReportWriter
{
    private static readonly JsonSerializerOptions s_opts = new() { WriteIndented = true };
    private readonly BlobContainerClient _container;

    public bool IsEnabled { get; }

    public RunReportWriter(BlobContainerClient container, IHostEnvironment env)
    {
        _container = container;
        IsEnabled  = env.IsDevelopment();
    }

    public Task WriteReportAsync<T>(string path, T report, CancellationToken ct = default) =>
        WriteAsync(path, report, ct);

    private const string LastIndexStatsPath = "indexing/_last-stats.json";

    public async Task<(long DocumentCount, long StorageSizeBytes)?> GetLastIndexStatsAsync(CancellationToken ct = default)
    {
        try
        {
            var stats = await ReadAsync<LastIndexStats>(LastIndexStatsPath, ct);
            return stats is null ? null : (stats.DocumentCount, stats.StorageSizeBytes);
        }
        catch
        {
            // Missing/corrupt baseline should never block the pipeline — just means no drift check this run.
            return null;
        }
    }

    public Task SaveLastIndexStatsAsync(long documentCount, long storageSizeBytes, CancellationToken ct = default) =>
        WriteAsync(LastIndexStatsPath, new LastIndexStats(documentCount, storageSizeBytes), ct);

    private record LastIndexStats(long DocumentCount, long StorageSizeBytes);

    private async Task WriteAsync<T>(string path, T data, CancellationToken ct)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: ct);
        var json = JsonSerializer.SerializeToUtf8Bytes(data, s_opts);
        using var ms = new MemoryStream(json);
        await _container.GetBlobClient(path).UploadAsync(ms, overwrite: true, cancellationToken: ct);
    }

    private async Task<T?> ReadAsync<T>(string path, CancellationToken ct)
    {
        var blob = _container.GetBlobClient(path);
        if (!await blob.ExistsAsync(ct))
            return default;

        var download = await blob.DownloadContentAsync(ct);
        return JsonSerializer.Deserialize<T>(download.Value.Content.ToMemory().Span, s_opts);
    }
}
