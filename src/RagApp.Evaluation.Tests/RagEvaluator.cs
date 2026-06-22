using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using ProtocolsIndexer.Services;
using RagApp.Evaluation.Tests.Models;

namespace RagApp.Evaluation.Tests.Evaluation;

/// <summary>
/// Calls the RAG app for a given TestQuery, scores the response with 4 evaluators
/// (run in parallel), and returns the result as an EvalRow. Does no I/O beyond the
/// ragCall itself — persistence is EvalResultWriter's job.
/// </summary>
public sealed class RagEvaluator
{
    private readonly GroundednessEvaluator _groundedness = new();
    private readonly RelevanceEvaluator _relevance = new();
    private readonly CoherenceEvaluator _coherence = new();
    private readonly EquivalenceEvaluator _equivalence = new();
    private readonly ChatConfiguration _judgeConfig;

    public RagEvaluator(IChatClient judgeClient)
    {
        _judgeConfig = new ChatConfiguration(judgeClient);
    }

    public async Task<EvalRow> RunAsync(
        TestQuery testQuery,
        Func<string, Task<RagQueryResult>> ragCall,
        CancellationToken ct = default)
    {
        var result = await ragCall(testQuery.Query);

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

        // Run all 4 judge calls in parallel instead of awaiting one by one.
        var groundednessTask = _groundedness.EvaluateAsync(messages, chatResponse, _judgeConfig, groundednessCtx, ct);
        var relevanceTask = _relevance.EvaluateAsync(messages, chatResponse, _judgeConfig, additionalContext: null, ct);
        var coherenceTask = _coherence.EvaluateAsync(messages, chatResponse, _judgeConfig, additionalContext: null, ct);
        var equivalenceTask = _equivalence.EvaluateAsync(messages, chatResponse, _judgeConfig, equivalenceCtx, ct);

        await Task.WhenAll(groundednessTask, relevanceTask, coherenceTask, equivalenceTask);

        return new EvalRow(
            ScenarioName: testQuery.Name,
            Department: testQuery.Department,
            Query: testQuery.Query,
            Difficulty: testQuery.Difficulty,
            ExpectedAnswer: testQuery.ExpectedAnswer,
            ExpectedSources: testQuery.ExpectedSources,
            Response: result.Answer,
            RetrievedContext: result.RetrievedContext,
            LatencyMs: result.LatencyMs,
            InputTokens: result.InputTokens,
            OutputTokens: result.OutputTokens,
            Groundedness: groundednessTask.Result.Get<NumericMetric>(GroundednessEvaluator.GroundednessMetricName)?.Value ?? 0,
            Relevance: relevanceTask.Result.Get<NumericMetric>(RelevanceEvaluator.RelevanceMetricName)?.Value ?? 0,
            Coherence: coherenceTask.Result.Get<NumericMetric>(CoherenceEvaluator.CoherenceMetricName)?.Value ?? 0,
            Equivalence: equivalenceTask.Result.Get<NumericMetric>(EquivalenceEvaluator.EquivalenceMetricName)?.Value ?? 0,
            Timestamp: DateTimeOffset.UtcNow);
    }
}