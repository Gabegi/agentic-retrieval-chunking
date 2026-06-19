using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.OpenAI;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtocolsIndexer.Configuration;
using ProtocolsIndexer.Services;

namespace RagApp.Evaluation.Tests;

[TestClass]
public class RagEvaluationTests
{
    private static RagEvaluator     _evaluator  = null!;
    private static IRagQueryService _ragService = null!;

    [ClassInitialize]
    public static Task ClassInit(TestContext _)
    {
        var credential = new DefaultAzureCredential();

        var config = new IndexerConfig
        {
            SearchEndpoint            = Env("SEARCH_ENDPOINT"),
            OpenAiEndpoint            = Env("OPENAI_ENDPOINT"),
            OpenAiEmbeddingDeployment = Env("OPENAI_EMBEDDING_DEPLOYMENT"),
            OpenAiGptDeployment       = Env("OPENAI_GPT_DEPLOYMENT"),
            OpenAiGptModelName        = Env("OPENAI_GPT_MODEL_NAME"),
            SearchIndexName           = Env("SEARCH_INDEX_NAME"),
            StorageAccountUrl         = Env("STORAGE_ACCOUNT_URL"),
            StorageContainer          = Env("STORAGE_CONTAINER"),
            KnowledgeSourceName       = Env("KNOWLEDGE_SOURCE_NAME"),
            KnowledgeBaseName         = Env("KNOWLEDGE_BASE_NAME"),
        };

        var openAi    = new AzureOpenAIClient(new Uri(config.OpenAiEndpoint), credential);
        var container = new BlobServiceClient(new Uri(config.StorageAccountUrl), credential)
                            .GetBlobContainerClient(config.StorageContainer);

        IChatClient judgeClient = openAi.GetChatClient(config.OpenAiGptDeployment).AsIChatClient();

        _ragService = new RagQueryService(openAi, config);
        _evaluator  = new RagEvaluator(judgeClient, container);

        return Task.CompletedTask;
    }

    // Dutch medical protocol golden queries — scores are 1–5, threshold ≥ 3 is "acceptable"
    [TestMethod]
    [DataRow("salmonella-uitbraak",
        "Wat zijn de maatregelen bij een uitbraak van salmonella in een kinderdagverblijf?")]
    [DataRow("tuberculose-isolatie",
        "Wanneer is isolatie vereist bij tuberculose en hoe lang duurt de besmettelijke periode?")]
    [DataRow("legionella-behandeling",
        "Welke antibiotica worden aanbevolen voor de behandeling van legionellose en wat zijn de doseringen?")]
    [DataRow("hepatitis-a-incubatie",
        "Wat zijn de incubatietijd en de klinische symptomen van hepatitis A?")]
    [DataRow("meningokokken-protocol",
        "Hoe moet een arts handelen bij vermoeden van meningokokkenziekte bij een kind?")]
    [DataRow("mazelen-bronopsporing",
        "Wat is het beleid voor bronopsporing en vaccinatie bij een bevestigd geval van mazelen?")]
    [DataRow("kinkhoest-profylaxe",
        "Wanneer is profylactische antibiotica geïndiceerd bij blootstelling aan kinkhoest?")]
    public async Task EvaluateQuery(string scenarioName, string query)
    {
        var row = await _evaluator.RunAsync(scenarioName, query, q => _ragService.AskAsync(q));

        Console.WriteLine(
            $"[{scenarioName}] latency={row.LatencyMs}ms  " +
            $"in={row.InputTokens} out={row.OutputTokens}  " +
            $"G={row.Groundedness:F1} R={row.Relevance:F1} C={row.Coherence:F1}");

        Assert.IsTrue(row.Groundedness >= 3,
            $"Groundedness {row.Groundedness:F1}/5 below threshold for '{scenarioName}'");
        Assert.IsTrue(row.Relevance >= 3,
            $"Relevance {row.Relevance:F1}/5 below threshold for '{scenarioName}'");
        Assert.IsTrue(row.Coherence >= 3,
            $"Coherence {row.Coherence:F1}/5 below threshold for '{scenarioName}'");
    }

    private static string Env(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Missing required env var: {name}");
}
