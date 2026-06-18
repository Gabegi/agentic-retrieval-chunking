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
    private readonly IEmbeddingService             _embeddingService;
    private readonly IIndexService                 _indexService;
    private readonly AzureOpenAIClient             _openAi;
    private readonly IndexerConfig                 _config;
    private readonly ILogger<PipelineOrchestrator> _logger;

    private bool _indexEnsured;

    public PipelineOrchestrator(
        BlobServiceClient               blobServiceClient,
        IEnumerable<IExtractionService> services,
        IEmbeddingService               embeddingService,
        IIndexService                   indexService,
        AzureOpenAIClient               openAi,
        IndexerConfig                   config,
        ILogger<PipelineOrchestrator>   logger)
    {
        _container        = blobServiceClient.GetBlobContainerClient(
            string.IsNullOrEmpty(config.StorageContainer) ? "protocols" : config.StorageContainer);
        _services         = services.ToArray();
        _embeddingService = embeddingService;
        _indexService     = indexService;
        _openAi           = openAi;
        _config           = config;
        _logger           = logger;
    }

    // ── Per-blob pipeline: extract → embed → index ────────────────────────
    public async Task ProcessBlobAsync(string blobName, byte[] bytes, CancellationToken ct = default)
    {
        try
        {
            if (!_indexEnsured)
            {
                await _indexService.EnsureIndexAsync();
                _indexEnsured = true;
            }

            var run = await _services[0].ExtractAsync(blobName, bytes, ct);
            if (run.Error != null)
            {
                _logger.LogError("Extraction failed for {Blob}: {Error}", blobName, run.Error);
                return;
            }
            _logger.LogInformation("{Blob} → {Chunks} chunks extracted", blobName, run.ChunkCount);

            var embedded = await _embeddingService.EmbedDocumentsAsync(run.Chunks, ct);
            await _embeddingService.UploadDocumentsAsync(embedded, ct);
            _logger.LogInformation("{Blob} indexed", blobName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline failed for {Blob}", blobName);
            throw;
        }
    }

    // ── Bulk run (backfill / local use) ───────────────────────────────────
    public async Task RunAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Bulk run using {Service}", _services[0].Name);
        await foreach (var (item, bytes) in StreamBlobsAsync(ct))
            await ProcessBlobAsync(item.Name, bytes, ct);
    }

    // ── Streaming blob download ───────────────────────────────────────────
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
