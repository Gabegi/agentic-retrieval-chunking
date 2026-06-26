using System.Net;
using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Models;
using ProtocolsIndexer.Services;

namespace ProtocolsIndexer;

// Generic indexing entrypoint. Source-agnostic: pass ?source=csv (or pdf, etc.)
// to select the registered extractor. The pipeline steps are the same for all sources.
//
// Source routing:
// • ?source=csv  → resolves CsvExtractionOrchestrator (Source = "csv")
// • ?source=pdf  → resolves PdfExtractionOrchestrator (Source = "pdf") once implemented
// To add a new source: implement IExtractionOrchestrator, set Source, register in program.cs.
//
// Payload pattern: extracted docs and chunks are written to blob (container: indexing-pipeline,
// paths: {instanceId}/extracted.json and {instanceId}/chunks.json). Only the blob name string
// travels through Durable Table Storage, avoiding the 64KB row-size limit.
public class IndexingFunction
{
    private readonly IIndexingPipelineOrchestrator _orchestrator;
    private readonly IKnowledgeService         _knowledgeService;
    private readonly BlobContainerClient       _pipelineContainer;
    private readonly ILogger<IndexingFunction> _logger;

    public IndexingFunction(
        IIndexingPipelineOrchestrator orchestrator,
        IKnowledgeService         knowledgeService,
        [FromKeyedServices("pipeline-temp")] BlobContainerClient pipelineContainer,
        ILogger<IndexingFunction> logger)
    {
        _orchestrator      = orchestrator;
        _knowledgeService  = knowledgeService;
        _pipelineContainer = pipelineContainer;
        _logger            = logger;
    }

    // POST /api/index?source=csv
    // No request body needed. The ?source param selects the registered extractor
    // (currently only "csv"). The source files (pages.csv, index.csv) are read
    // directly from the "documentscsv" blob container by the extractor.
    [Function("StartIndexing")]
    public async Task<HttpResponseData> Start(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "index")] HttpRequestData req,
        [DurableClient] DurableTaskClient client)
    {
        var source = req.Query["source"] ?? "csv";

        var instanceId = await client.ScheduleNewOrchestrationInstanceAsync("IndexingOrchestrator", source);
        _logger.LogInformation("Indexing started — source '{Source}', instance {InstanceId}", source, instanceId);
        return client.CreateCheckStatusResponse(req, instanceId);
    }

    [Function("IndexingOrchestrator")]
    public async Task RunOrchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var source     = context.GetInput<string>()!;
        // Durable Functions pass activity inputs/outputs through Azure Table Storage, which has a 64KB row size limit
        // Stores extaction inputs/outputs to blob to circumvent row size limit
        //  Only the blob path string (e.g. "abc123/extracted.json")travels through Table Storage.
        var docsBlob   = $"{context.InstanceId}/extracted.json"; 
        var chunksBlob = $"{context.InstanceId}/chunks.json";

        await context.CallActivityAsync("ExtractActivity",        new ExtractRequest(source, docsBlob));
        await context.CallActivityAsync("ChunkActivity",          new ChunkRequest(docsBlob, chunksBlob));
        await context.CallActivityAsync("EmbedAndUploadActivity", chunksBlob);
    }

    // Step 1 — run the source-specific extractor; serialise ExtractionDocuments to blob
    [Function("ExtractActivity")]
    public async Task ExtractActivity([ActivityTrigger] ExtractRequest req, FunctionContext context)
    {
        try
        {
            var docs = await _orchestrator.ExtractAsync(req.Source, context.CancellationToken);
            await WriteBlobAsync(req.OutputBlob, docs, context.CancellationToken);
            _logger.LogInformation("Extracted {Count} docs from '{Source}' → {Blob}", docs.Count, req.Source, req.OutputBlob);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ExtractActivity failed for '{Source}'", req.Source);
            throw new InvalidOperationException($"ExtractActivity failed: {ex.Message}");
        }
    }

    // Step 2 — read ExtractionDocuments, chunk, serialise ProtocolDocuments to blob; delete input blob
    [Function("ChunkActivity")]
    public async Task ChunkActivity([ActivityTrigger] ChunkRequest req, FunctionContext context)
    {
        try
        {
            var docs   = await ReadBlobAsync<List<ExtractionDocument>>(req.InputBlob, context.CancellationToken);
            var chunks = _orchestrator.Chunk(docs);
            await DeleteBlobAsync(req.InputBlob, context.CancellationToken);
            await WriteBlobAsync(req.OutputBlob, chunks, context.CancellationToken);
            _logger.LogInformation("Chunked {Docs} docs into {Chunks} chunks → {Blob}", docs.Count, chunks.Count, req.OutputBlob);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"ChunkActivity failed: {ex.Message}", ex);
        }
    }

    // Step 3 — read ProtocolDocuments, embed and upload to Azure AI Search; delete input blob
    [Function("EmbedAndUploadActivity")]
    public async Task EmbedAndUploadActivity([ActivityTrigger] string chunksBlobName, FunctionContext context)
    {
        try
        {
            var chunks = await ReadBlobAsync<List<ProtocolDocument>>(chunksBlobName, context.CancellationToken);
            await _orchestrator.EmbedAndUploadAsync(chunks, context.CancellationToken);
            await DeleteBlobAsync(chunksBlobName, context.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"EmbedAndUploadActivity failed: {ex.Message}", ex);
        }
    }

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

    private async Task WriteBlobAsync<T>(string blobPath, T data, CancellationToken ct)
    {
        await _pipelineContainer.CreateIfNotExistsAsync(cancellationToken: ct);
        var json = JsonSerializer.SerializeToUtf8Bytes(data);
        using var ms = new MemoryStream(json);
        await _pipelineContainer.GetBlobClient(blobPath).UploadAsync(ms, overwrite: true, cancellationToken: ct);
    }

    private async Task<T> ReadBlobAsync<T>(string blobPath, CancellationToken ct)
    {
        var response = await _pipelineContainer.GetBlobClient(blobPath).DownloadContentAsync(ct);
        return JsonSerializer.Deserialize<T>(response.Value.Content)!;
    }

    private Task DeleteBlobAsync(string blobPath, CancellationToken ct) =>
        _pipelineContainer.GetBlobClient(blobPath).DeleteIfExistsAsync(cancellationToken: ct);
}

// Input records for Durable activity functions — must be serializable by System.Text.Json.
public record ExtractRequest(string Source, string OutputBlob);
public record ChunkRequest(string InputBlob, string OutputBlob);
