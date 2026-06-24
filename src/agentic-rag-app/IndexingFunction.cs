using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Models;
using ProtocolsIndexer.Services;

namespace ProtocolsIndexer;

// Generic indexing entrypoint. Source-agnostic: pass ?source=csv (or pdf, etc.)
// to select the registered extractor. The pipeline steps are the same for all sources.
//
// How source routing works:
// • ?source=csv  → resolves CsvExtractionOrchestrator (Source = "csv")
// • ?source=pdf  → resolves PdfExtractionOrchestrator (Source = "pdf") once implemented
// The source value is just a routing key — the extractor itself knows where to find its data
// (e.g. CsvExtractionOrchestrator hardcodes the "documentscsv" container and blob names).
// To add a new source: implement IExtractionOrchestrator, set Source, register in program.cs.
public class IndexingFunction
{
    private readonly IRagPipelineOrchestrator  _orchestrator;
    private readonly IKnowledgeService         _knowledgeService;
    private readonly ILogger<IndexingFunction> _logger;

    public IndexingFunction(
        IRagPipelineOrchestrator  orchestrator,
        IKnowledgeService         knowledgeService,
        ILogger<IndexingFunction> logger)
    {
        _orchestrator     = orchestrator;
        _knowledgeService = knowledgeService;
        _logger           = logger;
    }

    // POST /api/index?source=csv — starts a Durable orchestration for the given source
    [Function("StartIndexing")]
    public async Task<HttpResponseData> Start(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "index")] HttpRequestData req,
        [DurableClient] DurableTaskClient client)
    {
        var source = req.Query["source"];
        if (string.IsNullOrWhiteSpace(source))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("'source' query parameter is required (e.g. ?source=csv)");
            return bad;
        }

        var instanceId = await client.ScheduleNewOrchestrationInstanceAsync("IndexingOrchestrator", source);
        _logger.LogInformation("Indexing started — source '{Source}', instance {InstanceId}", source, instanceId);
        return client.CreateCheckStatusResponse(req, instanceId);
    }

    [Function("IndexingOrchestrator")]
    public async Task RunOrchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var source = context.GetInput<string>()!;
        var docs   = await context.CallActivityAsync<List<ExtractionDocument>>("ExtractActivity", source);
        var chunks = await context.CallActivityAsync<List<ProtocolDocument>>("ChunkActivity", docs);
        await context.CallActivityAsync("EmbedAndUploadActivity", chunks);
    }

    // Step 1 — run the source-specific extractor, returns ExtractionDocuments
    [Function("ExtractActivity")]
    public async Task<List<ExtractionDocument>> ExtractActivity([ActivityTrigger] string source)
    {
        var docs = await _orchestrator.ExtractAsync(source);
        _logger.LogInformation("Extracted {Count} documents from '{Source}'", docs.Count, source);
        return [.. docs];
    }

    // Step 2 — chunk ExtractionDocuments into ProtocolDocuments (generic, source-agnostic)
    [Function("ChunkActivity")]
    public List<ProtocolDocument> ChunkActivity([ActivityTrigger] List<ExtractionDocument> docs)
        => [.. _orchestrator.Chunk(docs)];

    // Step 3 — embed vectors and upload to Azure AI Search
    [Function("EmbedAndUploadActivity")]
    public async Task EmbedAndUploadActivity([ActivityTrigger] List<ProtocolDocument> docs)
        => await _orchestrator.EmbedAndUploadAsync(docs);

    // POST /api/setup-knowledge-base — run once after the index is populated
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
