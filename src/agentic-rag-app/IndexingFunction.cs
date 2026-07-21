using System.Net;
using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgenticRag.Models;
using AgenticRag.Observability;
using AgenticRag.Observability.Reports;
using AgenticRag.Services;

namespace AgenticRag;

// PDF indexing entrypoint - Durable Functions orchestrator driving the
// extract/chunk/embed-and-upload pipeline.
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
    private readonly IPipelineArtifactWriter   _artifactWriter;
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
        IPipelineArtifactWriter   artifactWriter,
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
        _artifactWriter    = artifactWriter;
        _logger            = logger;
    }

    [Function("StartIndexing")]
    public async Task<HttpResponseData> Start(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "index")] HttpRequestData req,
        [DurableClient] DurableTaskClient client)
    {
        var forceReindex = req.Query["force"] == "true";

        var instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
            "IndexingOrchestrator", new IndexRequest(forceReindex));
        _logger.LogInformation("Indexing started — instance {InstanceId}", instanceId);
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
            extractResults = await context.CallActivityAsync<ExtractionResults>("ExtractActivity",        new ExtractRequest(input.ForceReindex, docsBlob, context.InstanceId));
            chunkResults   = await context.CallActivityAsync<ChunkingResults>("ChunkActivity",               new ChunkRequest(docsBlob, chunksBlob, context.InstanceId));
            embedResults   = await context.CallActivityAsync<EmbedUploadingResults>("EmbedAndUploadActivity", new EmbedUploadRequest(chunksBlob, extractResults.StaleDocumentIds, context.InstanceId));
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
                req.ForceReindex, context.CancellationToken);
            await WriteBlobAsync(req.OutputBlob, docs, context.CancellationToken);

            await _artifactWriter.WriteArtifactAsync(
                $"{req.InstanceId}/extraction.json", new { Docs = docs, Stats = stats }, context.CancellationToken);

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

            await _artifactWriter.WriteArtifactAsync(
                $"{req.InstanceId}/chunking.json", new { Chunks = chunks, Stats = stats }, context.CancellationToken);

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
    public async Task<EmbedUploadingResults> EmbedAndUploadActivity([ActivityTrigger] EmbedUploadRequest req, FunctionContext context)
    {
        try
        {
            var chunks = await ReadBlobAsync<List<ProtocolDocument>>(req.ChunksBlob, context.CancellationToken);
            LogProcessMemory("chunks loaded", chunks.Count);

            var sw              = System.Diagnostics.Stopwatch.StartNew();
            var embeddingResult = await _embeddingService.EmbedDocumentsAsync(chunks, context.CancellationToken);
            sw.Stop();
            LogProcessMemory("embedding complete", chunks.Count);

            // Metadata only, never the raw vectors (~12KB+ per chunk, and not useful to read
            // back as JSON anyway) - content_hash gets added here once Stage 3's hash-dedup lands.
            var chunkSummaries = embeddingResult.Documents
                .Select(d => new { d.Id, d.DocumentId, Dims = d.ContentVector?.Length });
            await _artifactWriter.WriteArtifactAsync(
                $"{req.InstanceId}/embedding.json",
                new
                {
                    Chunks = chunkSummaries,
                    Stats  = new
                    {
                        embeddingResult.ChunksTruncated,
                        embeddingResult.EmbeddingRetries,
                        embeddingResult.VectorDimErrors,
                    },
                },
                context.CancellationToken);

            var uploadResult = await _uploadService.UploadDocumentsAsync(
                embeddingResult.Documents, req.StaleDocumentIds, context.CancellationToken);
            LogProcessMemory("upload complete", chunks.Count);

            await DeleteBlobAsync(req.ChunksBlob, context.CancellationToken);

            return new EmbedUploadingResults(
                DocsUploaded:                  uploadResult.DocsUploaded,
                DocsFailed:                    uploadResult.DocsFailed,
                ChunksRemoved:                 uploadResult.ChunksRemoved,
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
            _logger.LogError(ex, "EmbedAndUploadActivity failed for '{ChunksBlob}'", req.ChunksBlob);
            throw new InvalidOperationException($"EmbedAndUploadActivity failed: {ex.Message}");
        }
    }

    [Function("SaveIndexReportActivity")]
    public async Task SaveIndexReportActivity([ActivityTrigger] IndexRunReport report, FunctionContext context)
    {
        if (!_reportWriter.IsEnabled) return;

        await _reportWriter.WriteReportAsync(
            $"indexing/{report.StartedAt:yyyy/MM/dd}/{report.InstanceId}.json", report, context.CancellationToken);
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
            Source:                  ext?.Source ?? "unknown",
            ForceReindex:            input.ForceReindex,
            Success:                 success,
            ErrorMessage:            error,
            DocsToProcess:           ext?.DocsToProcess          ?? 0,
            DocsSkipped:             ext?.DocsSkipped             ?? 0,
            DocsNew:                 ext?.DocsNew                 ?? 0,
            DocsUpdated:             ext?.DocsUpdated             ?? 0,
            DocsDeleted:             ext?.DocsDeleted             ?? 0,
            ChunksRemoved:           embed?.ChunksRemoved         ?? 0,
            ValidationErrors:        ext?.ValidationErrors        ?? 0,
            ValidationWarnings:      ext?.ValidationWarnings      ?? 0,
            ReconciliationProblems:  ext?.ReconciliationProblems  ?? 0,
            StaleDocCount:           ext?.StaleDocCount,
            MojibakeRepairedPages:   ext?.MojibakeRepairedPages   ?? 0,
            DetectedTableCount:      ext?.DetectedTableCount      ?? 0,
            DocsWithoutHeadings:     ext?.DocsWithoutHeadings     ?? 0,
            MissingTitleCount:       ext?.MissingTitleCount       ?? 0,
            MissingVersionCount:     ext?.MissingVersionCount,
            MissingDepartmentCount:  ext?.MissingDepartmentCount,
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

    // WorkingSet is the whole process's OS-level footprint (managed heap + native +
    // embedding vector arrays) - what actually counts against the EP1 plan's 3.5GB
    // ceiling. GC.GetTotalMemory is logged alongside it only to show how much of that
    // is the managed heap specifically, e.g. to tell "vectors held in memory" apart
    // from "native/runtime overhead" if the working set number looks high.
    private void LogProcessMemory(string stage, int chunkCount) =>
        _logger.LogInformation(
            "Memory @ {Stage} — {Chunks} chunks, working set {WorkingSetMb} MB, managed heap {HeapMb} MB",
            stage, chunkCount, Environment.WorkingSet / 1024 / 1024, GC.GetTotalMemory(false) / 1024 / 1024);
}

public record IndexRequest(bool ForceReindex);
public record ExtractRequest(bool ForceReindex, string OutputBlob, string InstanceId);
public record ChunkRequest(string InputBlob, string OutputBlob, string InstanceId);
public record EmbedUploadRequest(string ChunksBlob, IReadOnlyList<string> StaleDocumentIds, string InstanceId);
