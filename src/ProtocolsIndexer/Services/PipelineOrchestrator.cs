using Microsoft.Extensions.Logging;

namespace ProtocolsIndexer.Services;

public class PipelineOrchestrator : IPipelineOrchestrator
{
    private readonly IIndexService _indexService;
    private readonly IDocumentService _documentService;
    private readonly IEmbeddingService _embeddingService;
    private readonly IKnowledgeService _knowledgeService;
    private readonly ILogger<PipelineOrchestrator> _logger;

    public PipelineOrchestrator(
        IIndexService indexService,
        IDocumentService documentService,
        IEmbeddingService embeddingService,
        IKnowledgeService knowledgeService,
        ILogger<PipelineOrchestrator> logger)
    {
        _indexService     = indexService;
        _documentService  = documentService;
        _embeddingService = embeddingService;
        _knowledgeService = knowledgeService;
        _logger           = logger;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("Pipeline started");

        await _indexService.EnsureIndexAsync();

        var blobs    = await _documentService.ReadBlobsAsync(ct);
        var blobList = blobs.ToList();

        _logger.LogInformation("{Count} blobs to process", blobList.Count);

        var extracted    = await _documentService.ExtractDocumentsAsync(blobList, ct);
        var embedded     = await _embeddingService.EmbedDocumentsAsync(extracted, ct);
        var embeddedList = embedded.ToList();

        await _embeddingService.UploadDocumentsAsync(embeddedList, ct);

        await _knowledgeService.EnsureKnowledgeSourceAsync(ct);
        await _knowledgeService.EnsureKnowledgeBaseAsync(ct);

        _logger.LogInformation("Pipeline complete — {Count} documents indexed in {Ms}ms",
            embeddedList.Count, sw.ElapsedMilliseconds);
    }
}
