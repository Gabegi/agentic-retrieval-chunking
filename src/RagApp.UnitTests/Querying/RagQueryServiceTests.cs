using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.AI;
using Moq;
using AgenticRag.Configuration;
using AgenticRag.Services;

namespace RagApp.UnitTests.Querying;

[TestClass]
public class RagQueryServiceTests
{
    private static IndexerConfig Config() => new()
    {
        SearchEndpoint            = "https://search.example.com",
        OpenAiEndpoint            = "https://openai.example.com:443",
        OpenAiEmbeddingDeployment = "embed",
        StorageAccountUrl         = "https://storage.example.com",
        StorageContainer          = "container",
        SearchIndexName           = "index",
        KnowledgeSourceName       = "ks",
        KnowledgeBaseName         = "kb",
        OpenAiGptDeployment       = "gpt-deployment",
        OpenAiGptModelName        = "gpt-model",
    };

    private static SearchDocument Doc(string content) => new() { ["content"] = content };

    private static Mock<SearchClient> MockSearchClient(params string[] contents)
    {
        var mock = new Mock<SearchClient>();
        var results = SearchModelFactory.SearchResults(
            values: contents.Select(c => SearchModelFactory.SearchResult(Doc(c), 0.0, null)).ToList(),
            totalCount: (long)contents.Length,
            facets: null,
            coverage: null,
            rawResponse: Mock.Of<Response>());
        mock.Setup(c => c.SearchAsync<SearchDocument>(It.IsAny<string>(), It.IsAny<SearchOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(results, Mock.Of<Response>()));
        return mock;
    }

    private static Mock<IChatClient> MockChatClient(string answer, ChatFinishReason? finishReason = null, string? modelId = null)
    {
        var mock = new Mock<IChatClient>();
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, answer))
        {
            ModelId      = modelId ?? "gpt-model",
            FinishReason = finishReason ?? ChatFinishReason.Stop,
            Usage        = new UsageDetails { InputTokenCount = 10, OutputTokenCount = 20, TotalTokenCount = 30 },
        };
        mock.Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);
        return mock;
    }

    private static RagQueryService BuildService(Mock<IChatClient> chatClient, Mock<SearchClient> searchClient, IndexerConfig? config = null) =>
        new(chatClient.Object, searchClient.Object, config ?? Config());

    [TestMethod]
    public async Task AskAsync_ReturnsAnswerFromChatClient()
    {
        var service = BuildService(MockChatClient("The answer is 42."), MockSearchClient("some context"));

        var result = await service.AskAsync("What is the answer?");

        Assert.AreEqual("The answer is 42.", result.Answer);
    }

    [TestMethod]
    public async Task AskAsync_NoChunksRetrieved_SystemPromptHasNoDocumentsAppended()
    {
        var service = BuildService(MockChatClient("answer"), MockSearchClient());

        var result = await service.AskAsync("question");

        Assert.AreEqual(0, result.ChunksRetrieved);
        Assert.IsFalse(result.SystemInstructions.Contains("Geraadpleegde documenten"));
    }

    [TestMethod]
    public async Task AskAsync_ChunksRetrieved_AreJoinedIntoRetrievedContextAndAppendedToPrompt()
    {
        var service = BuildService(MockChatClient("answer"), MockSearchClient("chunk one", "chunk two"));

        var result = await service.AskAsync("question");

        Assert.AreEqual(2, result.ChunksRetrieved);
        Assert.IsTrue(result.RetrievedContext.Contains("chunk one"));
        Assert.IsTrue(result.RetrievedContext.Contains("chunk two"));
        Assert.IsTrue(result.SystemInstructions.Contains("Geraadpleegde documenten"));
    }

    [TestMethod]
    public async Task AskAsync_BlankContentChunks_AreExcludedFromRetrievedContext()
    {
        var service = BuildService(MockChatClient("answer"), MockSearchClient("real content", "   "));

        var result = await service.AskAsync("question");

        Assert.AreEqual(1, result.ChunksRetrieved);
    }

    [TestMethod]
    public async Task AskAsync_PopulatesUsageAndModelFieldsFromResponse()
    {
        var service = BuildService(MockChatClient("answer", modelId: "gpt-custom"), MockSearchClient());

        var result = await service.AskAsync("question");

        Assert.AreEqual("gpt-custom", result.Model);
        Assert.AreEqual(10, result.InputTokens);
        Assert.AreEqual(20, result.OutputTokens);
        Assert.AreEqual(30, result.TotalTokens);
        Assert.AreEqual("stop", result.FinishReason.ToLowerInvariant());
    }

    [TestMethod]
    public async Task AskAsync_SetsFixedSamplingOptions()
    {
        var service = BuildService(MockChatClient("answer"), MockSearchClient());

        var result = await service.AskAsync("question");

        Assert.AreEqual(0.0f, result.Temperature);
        Assert.AreEqual(2000, result.MaxOutputTokens);
        Assert.AreEqual(42L, result.Seed);
    }

    [TestMethod]
    public async Task AskAsync_ProviderAndOperationNameAreFixed()
    {
        var service = BuildService(MockChatClient("answer"), MockSearchClient());

        var result = await service.AskAsync("question");

        Assert.AreEqual("chat", result.OperationName);
        Assert.AreEqual("openai", result.ProviderName);
        Assert.AreEqual("openai.example.com", result.ServerAddress);
        Assert.AreEqual(443, result.ServerPort);
    }
}
