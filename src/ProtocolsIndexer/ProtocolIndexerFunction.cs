using System.Net;
using System.Text;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Services;

namespace ProtocolsIndexer;

public class ProtocolIndexerFunction
{
    private readonly IPipelineOrchestrator           _orchestrator;
    private readonly IKnowledgeService               _knowledgeService;
    private readonly BlobContainerClient             _container;
    private readonly QueueClient                     _queue;
    private readonly ILogger<ProtocolIndexerFunction> _logger;

    public ProtocolIndexerFunction(
        IPipelineOrchestrator            orchestrator,
        IKnowledgeService                knowledgeService,
        BlobContainerClient              container,
        QueueClient                      queue,
        ILogger<ProtocolIndexerFunction> logger)
    {
        _orchestrator     = orchestrator;
        _knowledgeService = knowledgeService;
        _container        = container;
        _queue            = queue;
        _logger           = logger;
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

    // Reindex all: POST /api/reindex
    // Enqueues every PDF blob name; each is processed independently via queue trigger.
    [Function("ReindexAll")]
    public async Task<HttpResponseData> RunReindexAll(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "reindex")] HttpRequestData req,
        FunctionContext context)
    {
        var limitStr = req.Query["limit"];
        var limit    = int.TryParse(limitStr, out var n) ? n : 5;

        _logger.LogInformation("ReindexAll triggered — enqueuing blobs (limit={Limit})", limit);

        await _queue.CreateIfNotExistsAsync();

        var enqueued = 0;
        await foreach (var blob in _container.GetBlobsAsync(cancellationToken: context.CancellationToken))
        {
            if (!blob.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) continue;
            if (enqueued >= limit) break;
            var message = Convert.ToBase64String(Encoding.UTF8.GetBytes(blob.Name));
            await _queue.SendMessageAsync(message, cancellationToken: context.CancellationToken);
            enqueued++;
            _logger.LogInformation("Enqueued {Name}", blob.Name);
        }

        var response = req.CreateResponse(HttpStatusCode.Accepted);
        await response.WriteStringAsync($"Enqueued {enqueued} blobs for reindexing");
        return response;
    }

    // Setup knowledge base: POST /api/setup-knowledge-base
    // Creates or updates the AI Search knowledge source and knowledge base. Run once after index is populated.
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

    // Queue trigger: processes one blob per invocation, independently retried by the runtime
    [Function("ProcessBlobFromQueue")]
    public async Task RunFromQueue(
        [QueueTrigger("%QUEUE_NAME%", Connection = "FuncStorage")] string message,
        FunctionContext context)
    {
        var blobName = Encoding.UTF8.GetString(Convert.FromBase64String(message));
        _logger.LogInformation("Queue trigger: {Name}", blobName);

        using var ms = new MemoryStream();
        await _container.GetBlobClient(blobName).DownloadToAsync(ms, context.CancellationToken);

        await _orchestrator.ProcessBlobAsync(blobName, ms.ToArray(), context.CancellationToken);
    }
}
