using System.Reflection;
using Azure;
using Azure.Core;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Configuration;
using AgenticRagApp.Services;

namespace RagApp.UnitTests.Indexing;

// IndexService builds its own SearchIndexClient internally from config + credential (no
// constructor seam). The real client is constructed normally (harmless — no network call
// happens until a method is invoked) and swapped for a mock via reflection on the private field.
[TestClass]
public class IndexServiceTests
{
    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(Guid.NewGuid().ToString(), DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(GetToken(requestContext, cancellationToken));
    }

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
        var service = new IndexService(Config(), new FakeCredential(), NullLogger<IndexService>.Instance);
        var client   = new Mock<SearchIndexClient>();
        var field    = typeof(IndexService).GetField("_indexClient", BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(service, client.Object);
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
