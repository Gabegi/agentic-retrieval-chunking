using Azure;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Services;

namespace RagApp.UnitTests.Indexing;

[TestClass]
public class IndexServiceTests
{
    private static IndexerConfig Config() => new()
    {
        SearchEndpoint            = "https://search.example.com",
        OpenAiEndpoint            = "https://openai.example.com",
        OpenAiEmbeddingDeployment = "embed",
        StorageAccountUrl         = "https://storage.example.com",
        StorageContainer          = "container",
        SearchIndexName           = "my-index",
        KnowledgeSourceName       = "ks",
        KnowledgeBaseName         = "kb",
        OpenAiGptDeployment       = "gpt",
        OpenAiGptModelName        = "gpt-model",
    };

    private static (IndexService Service, Mock<SearchIndexClient> Client) BuildService()
    {
        var client  = new Mock<SearchIndexClient>();
        var service = new IndexService(Config(), client.Object, NullLogger<IndexService>.Instance);
        return (service, client);
    }

    [TestMethod]
    public async Task EnsureIndexAsync_IndexAlreadyExists_DoesNotCreateOrUpdate()
    {
        var (service, client) = BuildService();
        client.Setup(c => c.GetIndexAsync("my-index", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(new SearchIndex("my-index"), Mock.Of<Response>()));

        await service.EnsureIndexAsync();

        client.Verify(c => c.CreateOrUpdateIndexAsync(
            It.IsAny<SearchIndex>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task EnsureIndexAsync_IndexMissing_CreatesIt()
    {
        var (service, client) = BuildService();
        client.Setup(c => c.GetIndexAsync("my-index", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "not found"));
        client.Setup(c => c.CreateOrUpdateIndexAsync(
                It.IsAny<SearchIndex>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(new SearchIndex("my-index"), Mock.Of<Response>()));

        await service.EnsureIndexAsync();

        client.Verify(c => c.CreateOrUpdateIndexAsync(
            It.Is<SearchIndex>(i => i.Name == "my-index"), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task EnsureIndexAsync_UnexpectedFailureCheckingExistence_PropagatesWithoutCreating()
    {
        var (service, client) = BuildService();
        client.Setup(c => c.GetIndexAsync("my-index", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(500, "server error"));

        await Assert.ThrowsExceptionAsync<RequestFailedException>(() => service.EnsureIndexAsync());

        client.Verify(c => c.CreateOrUpdateIndexAsync(
            It.IsAny<SearchIndex>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
