using System.Diagnostics;
using System.Net;
using Azure.AI.OpenAI;
using Azure.AI.OpenAI.Chat;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using ProtocolsIndexer.Configuration;

namespace ProtocolsIndexer;

public class QueryFunction
{
    private readonly AzureOpenAIClient       _openAiClient;
    private readonly IndexerConfig           _config;
    private readonly ILogger<QueryFunction>  _logger;

    public QueryFunction(
        AzureOpenAIClient      openAiClient,
        IndexerConfig          config,
        ILogger<QueryFunction> logger)
    {
        _openAiClient = openAiClient;
        _config       = config;
        _logger       = logger;
    }

    // POST /api/query   body: { "question": "..." }
    [Function("Query")]
    public async Task<HttpResponseData> RunQuery(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "query")] HttpRequestData req,
        FunctionContext context)
    {
        var body = await req.ReadFromJsonAsync<QueryRequest>();
        if (string.IsNullOrWhiteSpace(body?.Question))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("'question' is required");
            return bad;
        }

        var chatClient = _openAiClient.GetChatClient(_config.OpenAiGptDeployment);

        var options = new ChatCompletionOptions();
        options.AddDataSource(new AzureSearchChatDataSource
        {
            Endpoint       = new Uri(_config.SearchEndpoint),
            IndexName      = _config.SearchIndexName,
            Authentication = DataSourceAuthentication.FromSystemManagedIdentity(),
        });

        var sw = Stopwatch.StartNew();
        var completion = await chatClient.CompleteChatAsync(
            [new UserChatMessage(body.Question)],
            options,
            context.CancellationToken);
        sw.Stop();

        var answer       = completion.Value.Content[0].Text;
        var inputTokens  = completion.Value.Usage.InputTokenCount;
        var outputTokens = completion.Value.Usage.OutputTokenCount;
        var latencyMs    = sw.ElapsedMilliseconds;

        _logger.LogInformation(
            "Query telemetry: {LatencyMs}ms, in={In} tokens, out={Out} tokens",
            latencyMs, inputTokens, outputTokens);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            answer,
            telemetry = new { latency_ms = latencyMs, input_tokens = inputTokens, output_tokens = outputTokens }
        });
        return response;
    }

    private sealed record QueryRequest(string Question);
}
