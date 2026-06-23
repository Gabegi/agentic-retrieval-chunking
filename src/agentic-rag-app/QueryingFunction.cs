using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Services;

namespace ProtocolsIndexer;

public class QueryingFunction
{
    private readonly IRagQueryService          _ragService;
    private readonly ILogger<QueryingFunction>  _logger;

    public QueryingFunction(IRagQueryService ragService, ILogger<QueryingFunction> logger)
    {
        _ragService = ragService;
        _logger     = logger;
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

        var result = await _ragService.AskAsync(body.Question, context.CancellationToken);

        _logger.LogInformation(
            "Query telemetry: {LatencyMs}ms, in={In} tokens, out={Out} tokens",
            result.LatencyMs, result.InputTokens, result.OutputTokens);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            answer    = result.Answer,
            telemetry = new
            {
                latency_ms    = result.LatencyMs,
                input_tokens  = result.InputTokens,
                output_tokens = result.OutputTokens
            }
        });
        return response;
    }

    private sealed record QueryRequest(string Question);
}
