using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Infrastructure.Configuration;

namespace RagApp.UnitTests.Infrastructure.Search;

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

    private static (IndexService Service, Mock<ISearchIndexStore> Store) BuildService()
    {
        var store   = new Mock<ISearchIndexStore>();
        var service = new IndexService(Config(), store.Object, NullLogger<IndexService>.Instance);
        return (service, store);
    }

    [TestMethod]
    public async Task EnsureIndexAsync_BuildsIndexForConfiguredNameAndDelegatesToStore()
    {
        var (service, store) = BuildService();
        store.Setup(s => s.EnsureIndexAsync(It.IsAny<SearchIndex>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await service.EnsureIndexAsync();

        store.Verify(s => s.EnsureIndexAsync(
            It.Is<SearchIndex>(i => i.Name == "my-index"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task EnsureIndexAsync_IncludesTheContentVectorFieldSizedToConfiguredDimensions()
    {
        var (service, store) = BuildService();
        SearchIndex? captured = null;
        store.Setup(s => s.EnsureIndexAsync(It.IsAny<SearchIndex>(), It.IsAny<CancellationToken>()))
            .Callback<SearchIndex, CancellationToken>((i, _) => captured = i)
            .ReturnsAsync(true);

        await service.EnsureIndexAsync();

        Assert.IsNotNull(captured);
        var vectorField = captured!.Fields.Single(f => f.Name == "content_vector");
        Assert.AreEqual(3072, vectorField.VectorSearchDimensions);
    }

    [TestMethod]
    public async Task EnsureIndexAsync_IncludesNativePdfMetadataFields()
    {
        var (service, store) = BuildService();
        SearchIndex? captured = null;
        store.Setup(s => s.EnsureIndexAsync(It.IsAny<SearchIndex>(), It.IsAny<CancellationToken>()))
            .Callback<SearchIndex, CancellationToken>((i, _) => captured = i)
            .ReturnsAsync(true);

        await service.EnsureIndexAsync();

        Assert.IsNotNull(captured);

        var createdAt = captured!.Fields.Single(f => f.Name == "created_at");
        Assert.AreEqual(SearchFieldDataType.DateTimeOffset, createdAt.Type);
        Assert.IsTrue(createdAt.IsFilterable);
        Assert.IsTrue(createdAt.IsSortable);

        var modDate = captured.Fields.Single(f => f.Name == "mod_date");
        Assert.AreEqual(SearchFieldDataType.DateTimeOffset, modDate.Type);
        Assert.IsTrue(modDate.IsFilterable);
        Assert.IsTrue(modDate.IsSortable);

        var pageCount = captured.Fields.Single(f => f.Name == "page_count");
        Assert.AreEqual(SearchFieldDataType.Int32, pageCount.Type);
        Assert.IsTrue(pageCount.IsFilterable);
    }

    [TestMethod]
    public async Task RecreateIndexAsync_DeletesThenRecreatesTheConfiguredIndex()
    {
        var (service, store) = BuildService();
        store.Setup(s => s.DeleteIndexAsync("my-index", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        store.Setup(s => s.EnsureIndexAsync(It.IsAny<SearchIndex>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await service.RecreateIndexAsync();

        store.Verify(s => s.DeleteIndexAsync("my-index", It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.EnsureIndexAsync(
            It.Is<SearchIndex>(i => i.Name == "my-index"), It.IsAny<CancellationToken>()), Times.Once);
    }
}
