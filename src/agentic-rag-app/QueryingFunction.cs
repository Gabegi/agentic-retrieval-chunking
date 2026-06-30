using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Observability.Reports;
using ProtocolsIndexer.Services;

namespace ProtocolsIndexer;

public class QueryingFunction
{
    private readonly IRagQueryService          _ragService;
    private readonly IRunReportWriter          _reportWriter;
    private readonly ILogger<QueryingFunction> _logger;

    public QueryingFunction(IRagQueryService ragService, IRunReportWriter reportWriter, ILogger<QueryingFunction> logger)
    {
        _ragService   = ragService;
        _reportWriter = reportWriter;
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

        var timestamp = DateTimeOffset.UtcNow;
        var result    = await _ragService.AskAsync(body.Question, context.CancellationToken);

        _logger.LogInformation(
            "Query telemetry: {LatencyMs}ms, in={In} tokens, out={Out} tokens",
            result.LatencyMs, result.InputTokens, result.OutputTokens);

        if (_reportWriter.IsEnabled)
            await _reportWriter.WriteQueryReportAsync(new QueryRunReport(
                RunId:            Guid.NewGuid().ToString("N"),
                Timestamp:        timestamp,
                Question:         body.Question,
                Answer:           result.Answer,
                RetrievedContext: result.RetrievedContext,
                ChunksRetrieved:  result.ChunksRetrieved,
                Model:            result.Model,
                FinishReason:     result.FinishReason,
                LatencyMs:        result.LatencyMs,
                InputTokens:      result.InputTokens,
                OutputTokens:     result.OutputTokens,
                TotalTokens:      result.TotalTokens),
                context.CancellationToken);

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
