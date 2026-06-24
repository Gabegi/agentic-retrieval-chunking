using System.Text.Json;
using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.AI;
using ProtocolsIndexer.Configuration;
using ProtocolsIndexer.Services;
using RagApp.Evaluation.Tests.Evaluation;
using RagApp.Evaluation.Tests.Models;

namespace RagApp.Evaluation.Tests;

[TestClass]
public class RagEvaluationTests
{
    private static RagEvaluator _evaluator = null!;
    private static IRagQueryService _ragService = null!;
    private static EvalResultWriter _writer = null!;

    public TestContext TestContext { get; set; } = null!;

    // Only Groundedness hard-fails the build (factual/safety-critical).
    // Relevance/Coherence/Equivalence are still scored and stored, but tracked
    // as trends in the report rather than gating individual test runs.
    private const double MinGroundedness = 3.0;

    [ClassInitialize]
    public static Task ClassInit(TestContext context)
    {
        var credential = new DefaultAzureCredential();

        var config = new IndexerConfig
        {
            SearchEndpoint = Env("SEARCH_ENDPOINT"),
            OpenAiEndpoint = Env("OPENAI_ENDPOINT"),
            OpenAiEmbeddingDeployment = Env("OPENAI_EMBEDDING_DEPLOYMENT"),
            OpenAiGptDeployment = Env("OPENAI_GPT_DEPLOYMENT"),
            OpenAiGptModelName = Env("OPENAI_GPT_MODEL_NAME"),
            SearchIndexName = Env("SEARCH_INDEX_NAME"),
            StorageAccountUrl = Env("STORAGE_ACCOUNT_URL"),
            StorageContainer = Env("STORAGE_CONTAINER"),
            KnowledgeSourceName = Env("KNOWLEDGE_SOURCE_NAME"),
            KnowledgeBaseName = Env("KNOWLEDGE_BASE_NAME"),
        };

        var openAi = new AzureOpenAIClient(new Uri(config.OpenAiEndpoint), credential);
        var container = new BlobServiceClient(new Uri(config.StorageAccountUrl), credential)
            .GetBlobContainerClient(config.StorageContainer);

        IChatClient ragChatClient = openAi.GetChatClient(config.OpenAiGptDeployment).AsIChatClient();
        IChatClient judgeClient = openAi.GetChatClient(Env("OPENAI_EVAL_DEPLOYMENT")).AsIChatClient();

        _ragService = new RagQueryService(ragChatClient, config);
        _evaluator = new RagEvaluator(judgeClient);
        _writer = new EvalResultWriter(container, executionId: $"{DateTime.UtcNow:yyyyMMddTHHmmss}");

        return Task.CompletedTask;
    }

    [TestMethod]
    [DynamicData(nameof(TestQueries), DynamicDataSourceType.Property)]
    public async Task EvaluateQuery(TestQuery testQuery)
    {
        var row = await _evaluator.RunAsync(testQuery, q => _ragService.AskAsync(q));
        await _writer.WriteAsync(row);

        Console.WriteLine(
            $"[{row.ScenarioName}] G={row.Groundedness:F1} R={row.Relevance:F1} " +
            $"C={row.Coherence:F1} Eq={row.Equivalence:F1} Ret={row.Retrieval:F1} F1={row.F1:F2}  " +
            $"{row.LatencyMs}ms  ${row.CostUsd:F4}  in={row.InputTokens} out={row.OutputTokens}");

        Assert.IsTrue(row.Succeeded,
            $"RAG call failed for '{testQuery.Name}': {row.Error}");
        Assert.IsTrue(row.Groundedness >= MinGroundedness,
            $"Groundedness {row.Groundedness:F1}/5 below threshold for '{testQuery.Name}'");
    }

    // Merges the curated set with the generated set (if present) into one test list.
    public static IEnumerable<object[]> TestQueries
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "testdata");

            var curated = LoadFile(Path.Combine(dir, "original-test-queries.json"));

            var generatedPath = Path.Combine(dir, "test-queries-generated.json");
            var generated = File.Exists(generatedPath) ? LoadFile(generatedPath) : [];

            return curated.Concat(generated).Select(q => new object[] { q });
        }
    }

    private static List<TestQuery> LoadFile(string path) =>
        JsonSerializer.Deserialize<TestQuery[]>(File.ReadAllText(path))?.ToList() ?? [];

    private static string Env(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Missing required env var: {name}");
}