using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using ProtocolsIndexer.Services;

namespace RagApp.Evaluation.Tests;

public record EvalRow(
    string          ScenarioName,
    string          Query,
    string          Response,
    string          RetrievedContext,
    long            LatencyMs,
    int             InputTokens,
    int             OutputTokens,
    double          Groundedness,
    double          Relevance,
    double          Coherence,
    DateTimeOffset  Timestamp);

public class RagEvaluator
{
    private readonly GroundednessEvaluator _groundedness = new();
    private readonly RelevanceEvaluator   _relevance    = new();
    private readonly CoherenceEvaluator   _coherence    = new();
    private readonly ChatConfiguration    _judgeConfig;
    private readonly BlobContainerClient  _container;

    public RagEvaluator(IChatClient judgeClient, BlobContainerClient container)
    {
        _judgeConfig = new ChatConfiguration(judgeClient);
        _container   = container;
    }

    public async Task<EvalRow> RunAsync(
        string scenarioName,
        string query,
        Func<string, Task<RagQueryResult>> ragCall,
        CancellationToken ct = default)
    {
        var result = await ragCall(query);

        var chatResponse = new ChatResponse([new ChatMessage(ChatRole.Assistant, result.Answer)])
        {
            Usage = new UsageDetails
            {
                InputTokenCount  = result.InputTokens,
                OutputTokenCount = result.OutputTokens,
                TotalTokenCount  = result.InputTokens + result.OutputTokens
            }
        };

        var messages = new List<ChatMessage> { new(ChatRole.User, query) };
        var groundednessCtx = new List<EvaluationContext>
        {
            new GroundednessEvaluatorContext(result.RetrievedContext)
        };

        var groundednessResult = await _groundedness.EvaluateAsync(messages, chatResponse, _judgeConfig, groundednessCtx, ct);
        var relevanceResult    = await _relevance.EvaluateAsync(messages, chatResponse, _judgeConfig, additionalContext: null, ct);
        var coherenceResult    = await _coherence.EvaluateAsync(messages, chatResponse, _judgeConfig, additionalContext: null, ct);

        var row = new EvalRow(
            ScenarioName:    scenarioName,
            Query:           query,
            Response:        result.Answer,
            RetrievedContext: result.RetrievedContext,
            LatencyMs:       result.LatencyMs,
            InputTokens:     result.InputTokens,
            OutputTokens:    result.OutputTokens,
            Groundedness:    groundednessResult.Get<NumericMetric>(GroundednessEvaluator.GroundednessMetricName)?.Value ?? 0,
            Relevance:       relevanceResult.Get<NumericMetric>(RelevanceEvaluator.RelevanceMetricName)?.Value ?? 0,
            Coherence:       coherenceResult.Get<NumericMetric>(CoherenceEvaluator.CoherenceMetricName)?.Value ?? 0,
            Timestamp:       DateTimeOffset.UtcNow);

        await AppendToBlobAsync(row, ct);
        return row;
    }

    private async Task AppendToBlobAsync(EvalRow row, CancellationToken ct)
    {
        var blobName = $"eval-results/{DateTime.UtcNow:yyyy-MM-dd}.jsonl";
        var blob     = _container.GetAppendBlobClient(blobName);
        await blob.CreateIfNotExistsAsync(cancellationToken: ct);

        var line  = JsonSerializer.Serialize(row) + "\n";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(line));
        await blob.AppendBlockAsync(stream, cancellationToken: ct);
    }
}
