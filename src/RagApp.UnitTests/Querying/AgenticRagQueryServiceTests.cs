using System.ClientModel.Primitives;
using System.Text.Json;
using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.KnowledgeBases;
using Azure.Search.Documents.KnowledgeBases.Models;
using Azure.Search.Documents.Models;
using Moq;
using ProtocolsIndexer.Configuration;
using ProtocolsIndexer.Services;

namespace RagApp.UnitTests.Querying;

[TestClass]
public class AgenticRagQueryServiceTests
{
    private static IndexerConfig Config() => new()
    {
        SearchEndpoint            = "https://search.example.com",
        OpenAiEndpoint            = "https://openai.example.com",
        OpenAiEmbeddingDeployment = "embed",
        StorageAccountUrl         = "https://storage.example.com",
        StorageContainer          = "container",
        SearchIndexName           = "index",
        KnowledgeSourceName       = "ks",
        KnowledgeBaseName         = "kb",
        OpenAiGptDeployment       = "gpt",
        OpenAiGptModelName        = "gpt-model",
    };

    // KnowledgeBaseRetrievalResponse (and its nested reference/message models) are Azure SDK
    // response-only models (no public constructor, read-only collections) - built via
    // ModelReaderWriter from JSON, the SDK's documented pattern for constructing them in tests.
    private static KnowledgeBaseRetrievalResponse RetrievalResponse(
        IEnumerable<Dictionary<string, object?>> referenceSourceData, string answerText)
    {
        var payload = new Dictionary<string, object?>
        {
            ["references"] = referenceSourceData.Select(sd => new Dictionary<string, object?> { ["type"] = "searchIndex", ["sourceData"] = sd }).ToList(),
            ["response"]   = new[] { new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = new[] { new Dictionary<string, object?> { ["type"] = "text", ["text"] = answerText } } } },
            ["activity"]   = Array.Empty<object>(),
        };
        var json = JsonSerializer.Serialize(payload);
        return ModelReaderWriter.Read<KnowledgeBaseRetrievalResponse>(BinaryData.FromString(json))!;
    }

    private static Mock<SearchClient> MockSearchClientWithNoNeighbors()
    {
        var mock = new Mock<SearchClient>();
        var results = SearchModelFactory.SearchResults(
            values: new List<SearchResult<SearchDocument>>(), totalCount: 0L, facets: null, coverage: null, rawResponse: Mock.Of<Response>());
        mock.Setup(c => c.SearchAsync<SearchDocument>(It.IsAny<string>(), It.IsAny<SearchOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(results, Mock.Of<Response>()));
        return mock;
    }

    private static Mock<KnowledgeBaseRetrievalClient> MockRetrievalClient(
        IEnumerable<Dictionary<string, object?>> referenceSourceData, string answerText)
    {
        var response = RetrievalResponse(referenceSourceData, answerText);
        var mock = new Mock<KnowledgeBaseRetrievalClient>();
        mock.Setup(c => c.RetrieveAsync(It.IsAny<KnowledgeBaseRetrievalRequest>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(response, Mock.Of<Response>()));
        return mock;
    }

    private static AgenticRagQueryService BuildService(
        Mock<KnowledgeBaseRetrievalClient> client, Mock<SearchClient>? searchClient = null) =>
        new(Config(), client.Object, new ChunkNeighborExpander((searchClient ?? MockSearchClientWithNoNeighbors()).Object));

    [TestMethod]
    public async Task AskAsync_ReturnsAnswerFromResponseMessages()
    {
        var client  = MockRetrievalClient([], "The synthesized answer.");
        var service = BuildService(client);

        var result = await service.AskAsync("What is the answer?");

        Assert.AreEqual("The synthesized answer.", result.Answer);
    }

    [TestMethod]
    public async Task AskAsync_NoReferences_NoCitationsAndEmptyContext()
    {
        var client  = MockRetrievalClient([], "answer");
        var service = BuildService(client);

        var result = await service.AskAsync("question");

        Assert.AreEqual(0, result.Citations.Count);
        Assert.AreEqual(0, result.ChunksRetrieved);
    }

    [TestMethod]
    public async Task AskAsync_OneReferencePerDocument_ProducesOneCitationEach()
    {
        var references = new[]
        {
            new Dictionary<string, object?> { ["id"] = "c1", ["document_id"] = "doc1", ["title"] = "Doc One", ["content"] = "content one" },
            new Dictionary<string, object?> { ["id"] = "c2", ["document_id"] = "doc2", ["title"] = "Doc Two", ["content"] = "content two" },
        };
        var client  = MockRetrievalClient(references, "answer");
        var service = BuildService(client);

        var result = await service.AskAsync("question");

        Assert.AreEqual(2, result.Citations.Count);
        CollectionAssert.AreEquivalent(new[] { "doc1", "doc2" }, result.Citations.Select(c => c.DocumentId).ToList());
    }

    [TestMethod]
    public async Task AskAsync_MultipleReferencesSameDocument_ProducesOneCitation()
    {
        var references = new[]
        {
            new Dictionary<string, object?> { ["id"] = "c1", ["document_id"] = "doc1", ["content"] = "page one" },
            new Dictionary<string, object?> { ["id"] = "c2", ["document_id"] = "doc1", ["content"] = "page two" },
        };
        var client  = MockRetrievalClient(references, "answer");
        var service = BuildService(client);

        var result = await service.AskAsync("question");

        Assert.AreEqual(1, result.Citations.Count);
    }

    [TestMethod]
    public async Task AskAsync_ProviderAndOperationNameAreFixed()
    {
        var client  = MockRetrievalClient([], "answer");
        var service = BuildService(client);

        var result = await service.AskAsync("question");

        Assert.AreEqual("knowledge_base_retrieve", result.OperationName);
        Assert.AreEqual("azure_ai_search", result.ProviderName);
        Assert.AreEqual("search.example.com", result.ServerAddress);
        Assert.AreEqual("stop", result.FinishReason);
    }
}
