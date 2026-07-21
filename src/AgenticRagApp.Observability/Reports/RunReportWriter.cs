using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Hosting;
using AgenticRagApp.Infrastructure.Clients.Blob;

namespace AgenticRagApp.Observability.Reports;

public class RunReportWriter : IRunReportWriter
{
    private static readonly JsonSerializerOptions s_opts = new() { WriteIndented = true };

    private readonly IBlobStore         _blobStore;
    private readonly BlobContainerClient _container;

    public bool IsEnabled { get; }

    public RunReportWriter(IBlobStore blobStore, BlobContainerClient container, IHostEnvironment env)
    {
        _blobStore = blobStore;
        _container = container;
        IsEnabled  = env.IsDevelopment();
    }

    public Task WriteReportAsync<T>(string path, T report, CancellationToken ct = default) =>
        WriteAsync(path, report, ct);

    private static string LastIndexStatsPath(string source) => $"indexing/_last-stats-{source}.json";

    public async Task<(long DocumentCount, long StorageSizeBytes)?> GetLastIndexStatsAsync(string source, CancellationToken ct = default)
    {
        try
        {
            var (stats, _) = await _blobStore.TryReadJsonWithETagAsync<LastIndexStats>(_container, LastIndexStatsPath(source), ct);
            return stats is null ? null : (stats.DocumentCount, stats.StorageSizeBytes);
        }
        catch
        {
            // Missing/corrupt baseline should never block the pipeline — just means no drift check this run.
            return null;
        }
    }

    public Task SaveLastIndexStatsAsync(string source, long documentCount, long storageSizeBytes, CancellationToken ct = default) =>
        WriteAsync(LastIndexStatsPath(source), new LastIndexStats(documentCount, storageSizeBytes), ct);

    private record LastIndexStats(long DocumentCount, long StorageSizeBytes);

    private async Task WriteAsync<T>(string path, T data, CancellationToken ct)
    {
        await _blobStore.EnsureContainerExistsAsync(_container, ct);
        var json = JsonSerializer.Serialize(data, s_opts);
        await _blobStore.UploadAsync(_container, path, BinaryData.FromString(json), overwrite: true, ct);
    }
}
