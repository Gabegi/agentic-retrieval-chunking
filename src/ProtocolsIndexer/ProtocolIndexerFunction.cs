using System.Net;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Services;

namespace ProtocolsIndexer;

public class ProtocolIndexerFunction
{
    private readonly IPipelineOrchestrator            _orchestrator;
    private readonly BlobContainerClient              _container;
    private readonly ILogger<ProtocolIndexerFunction> _logger;

    public ProtocolIndexerFunction(
        IPipelineOrchestrator            orchestrator,
        BlobContainerClient              container,
        ILogger<ProtocolIndexerFunction> logger)
    {
        _orchestrator = orchestrator;
        _container    = container;
        _logger       = logger;
    }

    // Fires automatically when a PDF lands in the container
    [Function("ProtocolIndexer")]
    public async Task RunBlobTrigger(
        [BlobTrigger("%STORAGE_CONTAINER%/{name}", Connection = "ProtocolsStorage")] byte[] content,
        string name,
        FunctionContext context)
    {
        if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return;
        _logger.LogInformation("Blob trigger: {Name}", name);
        await _orchestrator.ProcessBlobAsync(name, content, context.CancellationToken);
    }

    // Manual trigger: POST /api/process/{blobName}
    [Function("ProcessBlobManual")]
    public async Task<HttpResponseData> RunManual(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "process/{*blobName}")] HttpRequestData req,
        string blobName,
        FunctionContext context)
    {
        _logger.LogInformation("Manual trigger: {Name}", blobName);

        using var ms = new MemoryStream();
        await _container.GetBlobClient(blobName).DownloadToAsync(ms, context.CancellationToken);

        await _orchestrator.ProcessBlobAsync(blobName, ms.ToArray(), context.CancellationToken);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteStringAsync($"Processed {blobName}");
        return response;
    }

    // Reindex all: POST /api/reindex?limit=5 (default 5; pass limit=0 for all)
    [Function("ReindexAll")]
    public async Task<HttpResponseData> RunReindexAll(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "reindex")] HttpRequestData req,
        FunctionContext context)
    {
        var limitStr = req.Query["limit"];
        var limit    = int.TryParse(limitStr, out var n) ? n : 5;
        if (limit == 0) limit = int.MaxValue;

        _logger.LogInformation("ReindexAll triggered (limit={Limit})", limit == int.MaxValue ? "all" : limit.ToString());

        var processed = 0;
        await foreach (var blob in _container.GetBlobsAsync(cancellationToken: context.CancellationToken))
        {
            if (!blob.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) continue;
            if (processed >= limit) break;

            using var ms = new MemoryStream();
            await _container.GetBlobClient(blob.Name).DownloadToAsync(ms, context.CancellationToken);
            await _orchestrator.ProcessBlobAsync(blob.Name, ms.ToArray(), context.CancellationToken);
            processed++;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteStringAsync($"Reindexed {processed} blobs");
        return response;
    }

    // Setup knowledge base: POST /api/setup-knowledge-base
    // Run once after the index is populated.
    [Function("SetupKnowledgeBase")]
    public async Task<HttpResponseData> RunSetupKnowledgeBase(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "setup-knowledge-base")] HttpRequestData req,
        FunctionContext context)
    {
        _logger.LogInformation("SetupKnowledgeBase triggered");

        await _knowledgeService.EnsureKnowledgeSourceAsync(context.CancellationToken);
        await _knowledgeService.EnsureKnowledgeBaseAsync(context.CancellationToken);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteStringAsync("Knowledge source and knowledge base created or updated");
        return response;
    }
}
