using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.KnowledgeBases.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Querying.Services;

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

    private static (KnowledgeService Service, Mock<ISearchIndexStore> Store) BuildService()
    {
        var store   = new Mock<ISearchIndexStore>();
        var service = new KnowledgeService(Config(), store.Object, NullLogger<KnowledgeService>.Instance);
        return (service, store);
    }

    [TestMethod]
    public async Task EnsureKnowledgeSourceAsync_CreatesOrUpdatesWithConfiguredName()
    {
        var (service, store) = BuildService();

        await service.EnsureKnowledgeSourceAsync();

        store.Verify(s => s.CreateOrUpdateKnowledgeSourceAsync(
            It.Is<SearchIndexKnowledgeSource>(ks => ks.Name == "my-knowledge-source"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task EnsureKnowledgeSourceAsync_SourceDataFieldsIncludeNativePdfMetadata()
    {
        var (service, store) = BuildService();
        SearchIndexKnowledgeSource? captured = null;
        store.Setup(s => s.CreateOrUpdateKnowledgeSourceAsync(It.IsAny<SearchIndexKnowledgeSource>(), It.IsAny<CancellationToken>()))
            .Callback<SearchIndexKnowledgeSource, CancellationToken>((ks, _) => captured = ks)
            .Returns(Task.CompletedTask);

        await service.EnsureKnowledgeSourceAsync();

        Assert.IsNotNull(captured);
        var fieldNames = ((SearchIndexKnowledgeSourceParameters)captured!.SearchIndexParameters)
            .SourceDataFields.Select(f => f.Name).ToList();
        CollectionAssert.IsSubsetOf(new[] { "page_count", "created_at", "mod_date" }, fieldNames);
    }

    [TestMethod]
    public async Task EnsureKnowledgeBaseAsync_CreatesOrUpdatesWithConfiguredName()
    {
        var (service, store) = BuildService();

        await service.EnsureKnowledgeBaseAsync();

        store.Verify(s => s.CreateOrUpdateKnowledgeBaseAsync(
            It.Is<KnowledgeBase>(kb => kb.Name == "my-knowledge-base"), It.IsAny<CancellationToken>()), Times.Once);
    }
}
