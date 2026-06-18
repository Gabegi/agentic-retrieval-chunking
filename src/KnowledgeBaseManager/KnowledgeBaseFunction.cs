using System.Net;
using KnowledgeBaseManager.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace KnowledgeBaseManager;

public class KnowledgeBaseFunction
{
    private readonly IKnowledgeService              _knowledgeService;
    private readonly ILogger<KnowledgeBaseFunction> _logger;

    public KnowledgeBaseFunction(
        IKnowledgeService              knowledgeService,
        ILogger<KnowledgeBaseFunction> logger)
    {
        _knowledgeService = knowledgeService;
        _logger           = logger;
    }

    // POST /api/setup — creates or updates the knowledge source then the knowledge base
    [Function("SetupKnowledgeBase")]
    public async Task<HttpResponseData> RunSetup(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "setup")] HttpRequestData req,
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
