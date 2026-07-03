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
using ProtocolsIndexer.Observability;
using ProtocolsIndexer.Observability.Reports;
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
    private readonly IExtractionService        _extractionService;
    private readonly IChunkingService          _chunkingService;
    private readonly IEmbeddingService         _embeddingService;
    private readonly IUploadService            _uploadService;
    private readonly IIndexService             _indexService;
    private readonly IKnowledgeService         _knowledgeService;
    private readonly BlobContainerClient       _pipelineContainer;
    private readonly IRunReportWriter          _reportWriter;
    private readonly ILogger<IndexingFunction> _logger;

    public IndexingFunction(
        IExtractionService        extractionService,
        IChunkingService          chunkingService,
        IEmbeddingService         embeddingService,
        IUploadService            uploadService,
        IIndexService             indexService,
        IKnowledgeService         knowledgeService,
        [FromKeyedServices("pipeline-temp")] BlobContainerClient pipelineContainer,
        IRunReportWriter          reportWriter,
        ILogger<IndexingFunction> logger)
    {
        _extractionService = extractionService;
        _chunkingService   = chunkingService;
        _embeddingService  = embeddingService;
        _uploadService     = uploadService;
        _indexService      = indexService;
        _knowledgeService  = knowledgeService;
        _pipelineContainer = pipelineContainer;
        _reportWriter      = reportWriter;
        _logger            = logger;
    }

    [Function("StartIndexing")]
    public async Task<HttpResponseData> Start(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "index")] HttpRequestData req,
        [DurableClient] DurableTaskClient client)
    {
        var source                 = req.Query["source"] ?? "csv";
        var forceReindex           = req.Query["force"] == "true";
        // Bypasses ONLY the magnitude-shift validation gate - never error-rate/reconciliation
        // checks. Use after confirming in the logs that a large record-count shift is
        // legitimate (e.g. a genuinely large import), not a broken/truncated export.
        var overrideMagnitudeCheck = req.Query["overrideMagnitudeCheck"] == "true";

        var instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
            "IndexingOrchestrator", new IndexRequest(source, forceReindex, overrideMagnitudeCheck));
        _logger.LogInformation("Indexing started — source '{Source}', instance {InstanceId}", source, instanceId);
        return client.CreateCheckStatusResponse(req, instanceId);
    }

    [Function("IndexingOrchestrator")]
    public async Task RunOrchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var startedAt = context.CurrentUtcDateTime;
        var input     = context.GetInput<IndexRequest>()!;
        var docsBlob   = $"{context.InstanceId}/extracted.json";
        var chunksBlob = $"{context.InstanceId}/chunks.json";

        ExtractionResults?  extractResults = null;
        ChunkingResults?       chunkResults   = null;
        EmbedUploadingResults? embedResults   = null;
        bool    success = false;
        string? error   = null;

        try
        {
            extractResults = await context.CallActivityAsync<ExtractionResults>("ExtractActivity",        new ExtractRequest(input.Source, input.ForceReindex, input.OverrideMagnitudeCheck, docsBlob));
            chunkResults   = await context.CallActivityAsync<ChunkingResults>("ChunkActivity",               new ChunkRequest(docsBlob, chunksBlob));
            embedResults   = await context.CallActivityAsync<EmbedUploadingResults>("EmbedAndUploadActivity", chunksBlob);
            success      = true;
        }
        catch (Exception ex)
        {
            error = ex.ToString();
        }

        // Always call the activity — checking _reportWriter.IsEnabled here would be an
        // injected-dependency read inside orchestrator code, which Durable Functions'
        // determinism rules warn against. The activity itself is the right place to check.
        await context.CallActivityAsync("SaveIndexReportActivity",
            BuildReport(context, startedAt, input, extractResults, chunkResults, embedResults, success, error));

        if (!success)
            throw new InvalidOperationException(error ?? "Indexing pipeline failed");
    }

    // Step 1 — ensure index exists, run the extractor, serialise docs to blob, return stats
    [Function("ExtractActivity")]
    public async Task<ExtractionResults> ExtractActivity([ActivityTrigger] ExtractRequest req, FunctionContext context)
    {
        try
        {
            await _indexService.EnsureIndexAsync();
            var (docs, stats) = await _extractionService.ExtractAsync(
                req.Source, req.ForceReindex, req.OverrideMagnitudeCheck, context.CancellationToken);
            await WriteBlobAsync(req.OutputBlob, docs, context.CancellationToken);
            _logger.LogInformation("Extracted {Count} docs → {Blob}", docs.Count, req.OutputBlob);
            return stats;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Instrumentation.PipelineFailures.Add(1, new KeyValuePair<string, object?>("stage", "extract"));
            _logger.LogError(ex, "ExtractActivity failed");
            throw new InvalidOperationException($"ExtractActivity failed: {ex.Message}");
        }
    }

    // Step 2 — read ExtractionDocuments, chunk, serialise ProtocolDocuments to blob; return stats
    [Function("ChunkActivity")]
    public async Task<ChunkingResults> ChunkActivity([ActivityTrigger] ChunkRequest req, FunctionContext context)
    {
        try
        {
            var docs           = await ReadBlobAsync<List<ExtractionDocument>>(req.InputBlob, context.CancellationToken);
            var (chunks, stats) = _chunkingService.ChunkDocuments(docs);
            await DeleteBlobAsync(req.InputBlob, context.CancellationToken);
            await WriteBlobAsync(req.OutputBlob, chunks, context.CancellationToken);
            _logger.LogInformation("Chunked {Docs} docs into {Chunks} chunks → {Blob}", docs.Count, chunks.Count, req.OutputBlob);
            return stats;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Instrumentation.PipelineFailures.Add(1, new KeyValuePair<string, object?>("stage", "chunk"));
            _logger.LogError(ex, "ChunkActivity failed for '{InputBlob}'", req.InputBlob);
            throw new InvalidOperationException($"ChunkActivity failed: {ex.Message}");
        }
    }

    // Step 3 — read ProtocolDocuments, embed then upload to Azure AI Search; return combined stats
    [Function("EmbedAndUploadActivity")]
    public async Task<EmbedUploadingResults> EmbedAndUploadActivity([ActivityTrigger] string chunksBlobName, FunctionContext context)
    {
        try
        {
            var chunks = await ReadBlobAsync<List<ProtocolDocument>>(chunksBlobName, context.CancellationToken);

            var sw              = System.Diagnostics.Stopwatch.StartNew();
            var embeddingResult = await _embeddingService.EmbedDocumentsAsync(chunks, context.CancellationToken);
            sw.Stop();

            var uploadResult = await _uploadService.UploadDocumentsAsync(embeddingResult.Documents, context.CancellationToken);

            await DeleteBlobAsync(chunksBlobName, context.CancellationToken);

            return new EmbedUploadingResults(
                DocsUploaded:                  uploadResult.DocsUploaded,
                DocsFailed:                    uploadResult.DocsFailed,
                ChunksTruncated:               embeddingResult.ChunksTruncated,
                EmbeddingRetries:              embeddingResult.EmbeddingRetries,
                VectorDimErrors:               embeddingResult.VectorDimErrors,
                TotalEmbeddingDurationMs:      sw.ElapsedMilliseconds,
                IndexDocumentCountSnapshot:    uploadResult.IndexDocumentCountSnapshot,
                IndexStorageSizeBytesSnapshot: uploadResult.IndexStorageSizeBytesSnapshot,
                RedFlags:                      uploadResult.RedFlags);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "EmbedAndUploadActivity failed for '{ChunksBlob}'", chunksBlobName);
            throw new InvalidOperationException($"EmbedAndUploadActivity failed: {ex.Message}");
        }
    }

    [Function("SaveIndexReportActivity")]
    public async Task SaveIndexReportActivity([ActivityTrigger] IndexRunReport report, FunctionContext context)
    {
        if (!_reportWriter.IsEnabled) return;

        await _reportWriter.WriteIndexReportAsync(report, context.CancellationToken);
        _logger.LogInformation(
            "Index run report saved — instance={InstanceId}, docs={Docs}, chunks={Chunks}, success={Success}",
            report.InstanceId, report.DocsToProcess, report.ChunksProduced, report.Success);
    }

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

    private static IndexRunReport BuildReport(
        TaskOrchestrationContext context,
        DateTimeOffset           startedAt,
        IndexRequest             input,
        ExtractionResults?         ext,
        ChunkingResults?              chunk,
        EmbedUploadingResults?        embed,
        bool                     success,
        string?                  error) => new(
            InstanceId:              context.InstanceId,
            StartedAt:               startedAt,
            FinishedAt:              context.CurrentUtcDateTime,
            Source:                  input.Source,
            ForceReindex:            input.ForceReindex,
            Success:                 success,
            ErrorMessage:            error,
            DocsToProcess:           ext?.DocsToProcess          ?? 0,
            DocsSkipped:             ext?.DocsSkipped             ?? 0,
            DocsNew:                 ext?.DocsNew                 ?? 0,
            DocsUpdated:             ext?.DocsUpdated             ?? 0,
            DocsDeleted:             ext?.DocsDeleted             ?? 0,
            ChunksRemoved:           ext?.ChunksRemoved           ?? 0,
            ValidationErrors:        ext?.ValidationErrors        ?? 0,
            ValidationWarnings:      ext?.ValidationWarnings      ?? 0,
            ReconciliationProblems:  ext?.ReconciliationProblems  ?? 0,
            StaleDocCount:           ext?.StaleDocCount           ?? 0,
            DocsWithoutHeadings:     ext?.DocsWithoutHeadings     ?? 0,
            MissingTitleCount:       ext?.MissingTitleCount       ?? 0,
            MissingVersionCount:     ext?.MissingVersionCount     ?? 0,
            MissingDepartmentCount:  ext?.MissingDepartmentCount  ?? 0,
            ChunksProduced:          chunk?.ChunksProduced        ?? 0,
            DocsWithZeroChunks:      chunk?.DocsWithZeroChunks    ?? 0,
            DuplicateChunks:         chunk?.DuplicateChunks       ?? 0,
            MinChunkSizeChars:       chunk?.MinChunkSizeChars     ?? 0,
            MaxChunkSizeChars:       chunk?.MaxChunkSizeChars     ?? 0,
            AvgChunkSizeChars:       chunk?.AvgChunkSizeChars     ?? 0,
            P95ChunkSizeChars:       chunk?.P95ChunkSizeChars     ?? 0,
            BandUnder100:            chunk?.BandUnder100          ?? 0,
            Band100To500:            chunk?.Band100To500          ?? 0,
            Band500To1500:           chunk?.Band500To1500         ?? 0,
            Band1500Plus:            chunk?.Band1500Plus          ?? 0,
            CoherentChunks:          chunk?.CoherentChunks        ?? 0,
            HeadingsDetected:        chunk?.HeadingsDetected      ?? 0,
            ChunksTruncated:         embed?.ChunksTruncated       ?? 0,
            EmbeddingRetries:        embed?.EmbeddingRetries      ?? 0,
            VectorDimErrors:         embed?.VectorDimErrors       ?? 0,
            TotalEmbeddingDurationMs: embed?.TotalEmbeddingDurationMs ?? 0,
            DocsUploaded:                    embed?.DocsUploaded                  ?? 0,
            DocsFailed:                      embed?.DocsFailed                    ?? 0,
            IndexDocumentCountSnapshot:      embed?.IndexDocumentCountSnapshot,
            IndexStorageSizeBytesSnapshot:   embed?.IndexStorageSizeBytesSnapshot,
            Issues:                  ext?.Issues         ?? [],
            RedFlags:                [.. ext?.RedFlags ?? [], .. embed?.RedFlags ?? []],
            SpotCheckSample:         ext?.SpotCheckSample ?? []);

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

public record IndexRequest(string Source, bool ForceReindex, bool OverrideMagnitudeCheck = false);
public record ExtractRequest(string Source, bool ForceReindex, bool OverrideMagnitudeCheck, string OutputBlob);
public record ChunkRequest(string InputBlob, string OutputBlob);
