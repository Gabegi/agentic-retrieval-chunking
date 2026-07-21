using System.Text.Json;
using Azure.Storage.Blobs;
using AgenticRagApp.Infrastructure.Clients.Blob;

namespace AgenticRagApp.Observability.Reports;

public class PipelineArtifactWriter : IPipelineArtifactWriter
{
    private static readonly JsonSerializerOptions s_opts = new() { WriteIndented = true };

    private readonly IBlobStore          _blobStore;
    private readonly BlobContainerClient _container;

    public PipelineArtifactWriter(IBlobStore blobStore, BlobContainerClient container)
    {
        _blobStore = blobStore;
        _container = container;
    }

    public async Task WriteArtifactAsync<T>(string path, T artifact, CancellationToken ct = default)
    {
        await _blobStore.EnsureContainerExistsAsync(_container, ct);
        var json = JsonSerializer.Serialize(artifact, s_opts);
        await _blobStore.UploadAsync(_container, path, BinaryData.FromString(json), overwrite: true, ct);
    }
}
