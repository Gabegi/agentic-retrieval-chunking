using System.Text.Json;
using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.KnowledgeBases;
using Azure.Storage.Blobs;
using Microsoft.Extensions.AI;
using AgenticRagApp.Configuration;
using AgenticRagApp.Services;
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
    // Relevance/Coherence/Equivalence/Retrieval/F1 are scored and stored but tracked
    // as trends in the report rather than gating individual test runs.
    private const double MinGroundedness = 3.0;

    [ClassInitialize]
    public static async Task ClassInit(TestContext context)
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

        // Cap output tokens so Azure's TPM estimate is prompt+500 instead of prompt+model-default (~4096).
        // Scoring evaluators emit a score + brief explanation; they never need more than ~300 tokens.
        IChatClient judgeClient = openAi.GetChatClient(Env("OPENAI_EVAL_DEPLOYMENT"))
            .AsIChatClient()
            .AsBuilder()
            .ConfigureOptions(o => o.MaxOutputTokens ??= 500)
            .Build();

        var knowledgeService = new KnowledgeService(config, credential,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<KnowledgeService>.Instance);
        await knowledgeService.EnsureKnowledgeSourceAsync();
        await knowledgeService.EnsureKnowledgeBaseAsync();

        var searchClient = new SearchClient(new Uri(config.SearchEndpoint), config.SearchIndexName, credential);
        var kbClient = new KnowledgeBaseRetrievalClient(new Uri(config.SearchEndpoint), config.KnowledgeBaseName, credential);
        var neighborExpander = new ChunkNeighborExpander(searchClient);
        _ragService = new AgenticRagQueryService(config, kbClient, neighborExpander);
        _evaluator = new RagEvaluator(judgeClient);
        _writer = new EvalResultWriter(container, executionId: $"{DateTime.UtcNow:yyyyMMddTHHmmss}");
    }

    [TestCleanup]
    public Task Throttle() => Task.Delay(TimeSpan.FromSeconds(5));

    [TestMethod]
    [TestCategory("golden")]
    [DynamicData(nameof(GoldenQueries))]
    public async Task EvaluateGoldenQuery(TestQuery testQuery)
    {
        var row = await _evaluator.RunAsync(testQuery, q => _ragService.AskAsync(q));
        await _writer.WriteAsync(row);

        Console.WriteLine(
            $"[{row.ScenarioName}] G={row.Groundedness:F1} R={row.Relevance:F1} C={row.Coherence:F1} Eq={row.Equivalence:F1} " +
            $"Ret={row.Retrieval:F1} F1={row.F1:F2} Cite={row.CitationMatch:F2}  " +
            $"{row.LatencyMs}ms  ${row.CostUsd:F4}  in={row.InputTokens} out={row.OutputTokens}  ok={row.Succeeded}");

        Assert.IsTrue(row.Succeeded,
            $"RAG call failed for '{testQuery.Name}': {row.Error}");
        Assert.IsTrue(row.Groundedness >= MinGroundedness,
            $"Groundedness {row.Groundedness:F1}/5 below threshold for '{testQuery.Name}'");
    }

    public static IEnumerable<object[]> GoldenQueries
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, "testdata", "golden-questions.json");
            return LoadFile(path).Select(q => new object[] { q });
        }
    }

    private static List<TestQuery> LoadFile(string path) =>
        JsonSerializer.Deserialize<TestQuery[]>(File.ReadAllText(path))
            ?.Where(q => !string.IsNullOrWhiteSpace(q.Query))
            .ToList() ?? [];

    // Resource names/endpoints are environment-specific and documented in .env.example
    // (not secrets, but subscription-specific values that rot quickly if baked into source).
    private static string Env(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException(
            $"Missing required env var: {name}. See .env.example for the full list of required variables.");
}