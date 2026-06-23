using System.Diagnostics;
using System.Runtime.CompilerServices;
using Azure.AI.OpenAI;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Configuration;
using ProtocolsIndexer.Models;
using ProtocolsIndexer.Observability;

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

    private volatile bool _indexEnsured;

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
        using var activity = Instrumentation.ActivitySource.StartActivity("ProcessBlob", ActivityKind.Internal);
        activity?.SetTag("blob.name", blobName);

        try
        {
            if (!_indexEnsured)
            {
                await _indexService.EnsureIndexAsync();
                _indexEnsured = true;
            }

            using var extractActivity = Instrumentation.ActivitySource.StartActivity("Extract", ActivityKind.Internal);
            var run = await _services[0].ExtractAsync(blobName, bytes, ct);
            extractActivity?.SetTag("chunks", run.ChunkCount);
            extractActivity?.SetTag("fallback", run.UsedFallback);

            if (run.Error != null)
            {
                activity?.SetStatus(ActivityStatusCode.Error, run.Error);
                Instrumentation.BlobsProcessed.Add(1,
                    new KeyValuePair<string, object?>("status", "extraction_failed"));
                _logger.LogError("Extraction failed for {Blob}: {Error}", blobName, run.Error);
                return;
            }

            Instrumentation.ChunksExtracted.Record(run.ChunkCount,
                new KeyValuePair<string, object?>("service", run.ServiceName),
                new KeyValuePair<string, object?>("fallback", run.UsedFallback));
            _logger.LogInformation("{Blob} → {Chunks} chunks extracted", blobName, run.ChunkCount);

            var embedded = await _embeddingService.EmbedDocumentsAsync(run.Chunks, ct);
            await _embeddingService.UploadDocumentsAsync(embedded, ct);

            Instrumentation.BlobsProcessed.Add(1,
                new KeyValuePair<string, object?>("status", "success"));
            _logger.LogInformation("{Blob} indexed", blobName);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            Instrumentation.BlobsProcessed.Add(1,
                new KeyValuePair<string, object?>("status", "error"));
            _logger.LogError("Pipeline failed for {Blob} — {Type}: {Message}\n{Stack}",
                blobName, ex.GetType().Name, ex.Message, ex.StackTrace);
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
