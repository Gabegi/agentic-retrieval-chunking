using System.Net;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Services;

namespace ProtocolsIndexer;

public class ProtocolIndexerFunction
{
    private readonly IPipelineOrchestrator           _orchestrator;
    private readonly BlobContainerClient             _container;
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
    // Use for backfill or re-indexing a specific PDF.
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

    // Reindex all: POST /api/reindex
    // Re-processes every PDF in the container.
    [Function("ReindexAll")]
    public async Task<HttpResponseData> RunReindexAll(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "reindex")] HttpRequestData req,
        FunctionContext context)
    {
        _logger.LogInformation("Reindex all triggered");
        await _orchestrator.RunAsync(context.CancellationToken);
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteStringAsync("Reindex complete");
        return response;
    }
}
