using Azure;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.KnowledgeBases;
using Azure.Search.Documents.KnowledgeBases.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Services;

namespace RagApp.UnitTests.Querying;

[TestClass]
public class KnowledgeServiceTests
{
    private static IndexerConfig Config() => new()
    {
        SearchEndpoint            = "https://search.example.com",
        OpenAiEndpoint            = "https://openai.example.com",
        OpenAiEmbeddingDeployment = "embed",
        StorageAccountUrl         = "https://storage.example.com",
        StorageContainer          = "container",
        SearchIndexName           = "my-index",
        KnowledgeSourceName       = "my-knowledge-source",
        KnowledgeBaseName         = "my-knowledge-base",
        OpenAiGptDeployment       = "gpt",
        OpenAiGptModelName        = "gpt-model",
    };

    private static (KnowledgeService Service, Mock<SearchIndexClient> Client) BuildService()
    {
        var client  = new Mock<SearchIndexClient>();
        var service = new KnowledgeService(Config(), client.Object, NullLogger<KnowledgeService>.Instance);
        return (service, client);
    }

    [TestMethod]
    public async Task EnsureKnowledgeSourceAsync_CreatesOrUpdatesWithConfiguredName()
    {
        var (service, client) = BuildService();
        client.Setup(c => c.CreateOrUpdateKnowledgeSourceAsync(It.IsAny<SearchIndexKnowledgeSource>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SearchIndexKnowledgeSource ks, bool _, CancellationToken _) => Response.FromValue<KnowledgeSource>(ks, Mock.Of<Response>()));

        await service.EnsureKnowledgeSourceAsync();

        client.Verify(c => c.CreateOrUpdateKnowledgeSourceAsync(
            It.Is<SearchIndexKnowledgeSource>(ks => ks.Name == "my-knowledge-source"), false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task EnsureKnowledgeBaseAsync_CreatesOrUpdatesWithConfiguredName()
    {
        var (service, client) = BuildService();
        client.Setup(c => c.CreateOrUpdateKnowledgeBaseAsync(It.IsAny<KnowledgeBase>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((KnowledgeBase kb, bool _, CancellationToken _) => Response.FromValue(kb, Mock.Of<Response>()));

        await service.EnsureKnowledgeBaseAsync();

        client.Verify(c => c.CreateOrUpdateKnowledgeBaseAsync(
            It.Is<KnowledgeBase>(kb => kb.Name == "my-knowledge-base"), false, It.IsAny<CancellationToken>()), Times.Once);
    }
}
