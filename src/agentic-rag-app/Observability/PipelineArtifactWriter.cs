using System.Text.Json;
using Azure.Storage.Blobs;

namespace AgenticRag.Observability.Reports;

public class PipelineArtifactWriter : IPipelineArtifactWriter
{
    private static readonly JsonSerializerOptions s_opts = new() { WriteIndented = true };
    private readonly BlobContainerClient _container;

    public PipelineArtifactWriter(BlobContainerClient container)
    {
        _container = container;
    }

    public async Task WriteArtifactAsync<T>(string path, T artifact, CancellationToken ct = default)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: ct);
        var json = JsonSerializer.SerializeToUtf8Bytes(artifact, s_opts);
        using var ms = new MemoryStream(json);
        await _container.GetBlobClient(path).UploadAsync(ms, overwrite: true, cancellationToken: ct);
    }
}
