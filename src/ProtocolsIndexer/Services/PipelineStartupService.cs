using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ProtocolsIndexer.Services;

public class PipelineStartupService : IHostedService
{
    private readonly IIndexService     _indexService;
    private readonly IKnowledgeService _knowledgeService;
    private readonly ILogger<PipelineStartupService> _logger;

    public PipelineStartupService(
        IIndexService                    indexService,
        IKnowledgeService                knowledgeService,
        ILogger<PipelineStartupService>  logger)
    {
        _indexService     = indexService;
        _knowledgeService = knowledgeService;
        _logger           = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("Ensuring search index and knowledge base");
        await _indexService.EnsureIndexAsync();
        await _knowledgeService.EnsureKnowledgeSourceAsync(ct);
        await _knowledgeService.EnsureKnowledgeBaseAsync(ct);
        _logger.LogInformation("Infrastructure ready");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
