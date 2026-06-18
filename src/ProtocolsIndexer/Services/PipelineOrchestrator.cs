using System.Runtime.CompilerServices;
using Azure.AI.OpenAI;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Configuration;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public partial class PipelineOrchestrator : IPipelineOrchestrator
{
    private readonly BlobContainerClient           _container;
    private readonly IExtractionService[]          _services;
    private readonly AzureOpenAIClient             _openAi;
    private readonly IndexerConfig                 _config;
    private readonly ILogger<PipelineOrchestrator> _logger;

    public PipelineOrchestrator(
        BlobServiceClient               blobServiceClient,
        IEnumerable<IExtractionService> services,
        AzureOpenAIClient               openAi,
        IndexerConfig                   config,
        ILogger<PipelineOrchestrator>   logger)
    {
        _container = blobServiceClient.GetBlobContainerClient(
            string.IsNullOrEmpty(config.StorageContainer) ? "protocols" : config.StorageContainer);
        _services  = services.ToArray();
        _openAi    = openAi;
        _config    = config;
        _logger    = logger;
    }

    // ── Run mode ─────────────────────────────────────────────────────────
    public async Task RunAsync(CancellationToken ct = default)
    {
        var service = _services.Single();
        _logger.LogInformation("Pipeline using {Service}", service.Name);

        await foreach (var (item, bytes) in StreamBlobsAsync(ct))
        {
            var run = await service.ExtractAsync(item, bytes, ct);
            if (run.Error != null)
            {
                _logger.LogError("Extraction failed for {Blob}: {Error}", item.Name, run.Error);
                continue;
            }
            _logger.LogInformation("{Blob} → {Chunks} chunks", item.Name, run.ChunkCount);

            // TODO: embed and index
        }
    }

    // ── Streaming blob download (one PDF at a time, not all in RAM) ───────
    private async IAsyncEnumerable<(BlobItem Item, byte[] Bytes)> StreamBlobsAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in _container.GetBlobsAsync(cancellationToken: ct))
        {
            if (!item.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) continue;
            using var ms = new MemoryStream();
            await _container.GetBlobClient(item.Name).DownloadToAsync(ms, ct);
            yield return (item, ms.ToArray());
        }
    }
}
