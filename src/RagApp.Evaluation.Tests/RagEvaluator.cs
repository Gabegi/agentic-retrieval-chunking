using System.ClientModel;
using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.NLP;
using Microsoft.Extensions.AI.Evaluation.Quality;
using ProtocolsIndexer.Models;
using ProtocolsIndexer.Services;
using RagApp.Evaluation.Tests.Models;

namespace RagApp.Evaluation.Tests.Evaluation;

/// <summary>
/// Calls the RAG app for a given TestQuery, scores the response with 4 evaluators
/// (run in parallel), and returns the result as an EvalRow. Does no I/O beyond the
/// ragCall itself — persistence is EvalResultWriter's job.
///
/// We deliberately evaluate the OUTCOME (final answer) only. The agentic evaluators
/// (IntentResolution, TaskAdherence, ToolCallAccuracy) are skipped: they need the
/// agent's internal tool-call trace, which our single-turn test data does not carry.
/// </summary>
public sealed class RagEvaluator
{
    // GPT-4.1 list pricing (USD per 1 M tokens) — update when model changes.
    private const double InputUsdPerMToken  = 2.00;
    private const double OutputUsdPerMToken = 8.00;

    private readonly GroundednessEvaluator _groundedness = new();
    private readonly RelevanceEvaluator   _relevance    = new();
    private readonly CoherenceEvaluator   _coherence    = new();
    private readonly EquivalenceEvaluator _equivalence  = new();
    private readonly RetrievalEvaluator   _retrieval    = new();
    private readonly F1Evaluator          _f1           = new();
    private readonly ChatConfiguration    _judgeConfig;

    public RagEvaluator(IChatClient judgeClient)
    {
        // Cap output tokens so Azure's TPM estimate is prompt+500 instead of prompt+model-default (~4096).
        // Scoring evaluators emit a score + brief explanation; they never need more than ~300 tokens.
        _judgeConfig = new ChatConfiguration(judgeClient, new ChatOptions { MaxOutputTokens = 500 });
    }

    public async Task<EvalRow> RunAsync(
        TestQuery testQuery,
        Func<string, Task<RagQueryResult>> ragCall,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        RagQueryResult result;
        try
        {
            result = await ragCall(testQuery.Query);
            sw.Stop();
        }
        catch (Exception ex)
        {
            sw.Stop();
            return EvalRow.ForFailure(testQuery, ex.Message, sw.ElapsedMilliseconds);
        }

        var costUsd = (result.InputTokens * InputUsdPerMToken + result.OutputTokens * OutputUsdPerMToken) / 1_000_000.0;

        var chatResponse = new ChatResponse([new ChatMessage(ChatRole.Assistant, result.Answer)])
        {
            Usage = new UsageDetails
            {
                InputTokenCount = result.InputTokens,
                OutputTokenCount = result.OutputTokens,
                TotalTokenCount = result.InputTokens + result.OutputTokens
            }
        };

        var messages = new List<ChatMessage> { new(ChatRole.User, testQuery.Query) };

        var groundednessCtx = new List<EvaluationContext>
        {
            new GroundednessEvaluatorContext(result.RetrievedContext)
        };
        var equivalenceCtx = new List<EvaluationContext>
        {
            new EquivalenceEvaluatorContext(testQuery.ExpectedAnswer)
        };
        var retrievalCtx = new List<EvaluationContext>
        {
            new RetrievalEvaluatorContext(result.RetrievedContext)
        };
        var f1Ctx = new List<EvaluationContext>
        {
            new F1EvaluatorContext(testQuery.ExpectedAnswer)
        };

        // Run evaluators sequentially with 2 s gaps; retry handles residual 429s via Retry-After headers.
        var groundednessResult = await JudgeAsync(() => _groundedness.EvaluateAsync(messages, chatResponse, _judgeConfig, groundednessCtx, ct).AsTask(), ct);
        await Task.Delay(2000, ct);
        var relevanceResult    = await JudgeAsync(() => _relevance.EvaluateAsync(messages, chatResponse, _judgeConfig, additionalContext: null, ct).AsTask(), ct);
        await Task.Delay(2000, ct);
        var coherenceResult    = await JudgeAsync(() => _coherence.EvaluateAsync(messages, chatResponse, _judgeConfig, additionalContext: null, ct).AsTask(), ct);
        await Task.Delay(2000, ct);
        var equivalenceResult  = await JudgeAsync(() => _equivalence.EvaluateAsync(messages, chatResponse, _judgeConfig, equivalenceCtx, ct).AsTask(), ct);
        await Task.Delay(2000, ct);
        var retrievalResult    = await JudgeAsync(() => _retrieval.EvaluateAsync(messages, chatResponse, _judgeConfig, retrievalCtx, ct).AsTask(), ct);
        var f1Result           = await _f1.EvaluateAsync(messages, chatResponse, null, f1Ctx, ct);

        return new EvalRow(
            ScenarioName:    testQuery.Name,
            Department:      testQuery.Department,
            Query:           testQuery.Query,
            Difficulty:      testQuery.Difficulty,
            ExpectedAnswer:  testQuery.ExpectedAnswer,
            ExpectedSources: testQuery.ExpectedSources,
            Response:        result.Answer,
            RetrievedContext: result.RetrievedContext,
            Succeeded:       true,
            Error:           "",
            LatencyMs:       result.LatencyMs,
            InputTokens:     result.InputTokens,
            OutputTokens:    result.OutputTokens,
            CostUsd:         costUsd,
            Groundedness: groundednessResult.Get<NumericMetric>(GroundednessEvaluator.GroundednessMetricName)?.Value ?? 0,
            Relevance:    relevanceResult.Get<NumericMetric>(RelevanceEvaluator.RelevanceMetricName)?.Value ?? 0,
            Coherence:    coherenceResult.Get<NumericMetric>(CoherenceEvaluator.CoherenceMetricName)?.Value ?? 0,
            Equivalence:  equivalenceResult.Get<NumericMetric>(EquivalenceEvaluator.EquivalenceMetricName)?.Value ?? 0,
            Retrieval:    retrievalResult.Get<NumericMetric>(RetrievalEvaluator.RetrievalMetricName)?.Value ?? 0,
            F1:           f1Result.Get<NumericMetric>(F1Evaluator.F1MetricName)?.Value ?? 0,
            Timestamp:    DateTimeOffset.UtcNow);
    }

    // Retries a judge LLM call on 429, honouring the retry-after-ms header when present,
    // falling back to exponential back-off (4 → 8 → 16 → 32 s).
    private static async Task<EvaluationResult> JudgeAsync(
        Func<Task<EvaluationResult>> call, CancellationToken ct)
    {
        const int maxAttempts = 5;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (ClientResultException ex) when (ex.Status == 429 && attempt < maxAttempts - 1)
            {
                var delay = ParseRetryAfter(ex) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 2));
                await Task.Delay(delay, ct);
            }
        }
    }

    private static TimeSpan? ParseRetryAfter(ClientResultException ex)
    {
        var raw = ex.GetRawResponse();
        if (raw is null) return null;

        if (raw.Headers.TryGetValue("retry-after-ms", out var ms) && double.TryParse(ms, out var msVal))
            return TimeSpan.FromMilliseconds(msVal + 250);

        if (raw.Headers.TryGetValue("Retry-After", out var sec) && double.TryParse(sec, out var secVal))
            return TimeSpan.FromSeconds(secVal + 1);

        return null;
    }
}