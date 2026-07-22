using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Infrastructure.Configuration;

namespace RagApp.UnitTests.Infrastructure.Search;

[TestClass]
public class IndexDocumentServiceTests
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

    private static (IndexDocumentService Service, Mock<ISearchDocumentStore> DocumentStore, Mock<ISearchIndexStore> IndexStore)
        BuildService()
    {
        var documentStore = new Mock<ISearchDocumentStore>();
        var indexStore    = new Mock<ISearchIndexStore>();
        var service = new IndexDocumentService(Config(), documentStore.Object, indexStore.Object, NullLogger<IndexDocumentService>.Instance);

        return (service, documentStore, indexStore);
    }

    private sealed record FakeUploadChunk(string Id);

    [TestMethod]
    public async Task UpsertDocumentsAsync_DelegatesToStoreForWhateverDocumentTypeIsPassed()
    {
        var (service, documentStore, _) = BuildService();
        documentStore
            .Setup(s => s.UpsertDocumentsAsync(It.IsAny<IEnumerable<FakeUploadChunk>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((3, 1, 1));

        var (succeeded, failed) = await service.UpsertDocumentsAsync([
            new FakeUploadChunk("c1"),
            new FakeUploadChunk("c2"),
        ]);

        Assert.AreEqual(3, succeeded);
        Assert.AreEqual(1, failed);
    }

    [TestMethod]
    public async Task GetCurrentIndexedDocumentDatesAsync_DelegatesToStore()
    {
        var (service, documentStore, _) = BuildService();
        var expected = new Dictionary<string, DateTimeOffset> { ["doc1"] = DateTimeOffset.UtcNow };
        documentStore.Setup(s => s.GetCurrentIndexedDocumentDatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var result = await service.GetCurrentIndexedDocumentDatesAsync();

        Assert.AreSame(expected, result);
    }

    [TestMethod]
    public async Task GetChunkIdsForDocumentsAsync_DelegatesToStore()
    {
        var (service, documentStore, _) = BuildService();
        documentStore.Setup(s => s.GetChunkIdsForDocumentsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(["c1", "c2"]);

        var result = await service.GetChunkIdsForDocumentsAsync(["doc1"]);

        CollectionAssert.AreEqual(new[] { "c1", "c2" }, result.ToList());
    }

    [TestMethod]
    public async Task DeleteChunksByIdAsync_DelegatesToStore()
    {
        var (service, documentStore, _) = BuildService();
        documentStore.Setup(s => s.DeleteChunksByIdAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(2);

        var result = await service.DeleteChunksByIdAsync(["c1", "c2"]);

        Assert.AreEqual(2, result);
    }

    [TestMethod]
    public async Task GetStatisticsAsync_DelegatesToIndexStoreWithConfiguredIndexName()
    {
        var (service, _, indexStore) = BuildService();
        indexStore.Setup(s => s.GetStatisticsAsync("index", It.IsAny<CancellationToken>())).ReturnsAsync((100L, 2048L));

        var (docCount, storageBytes) = await service.GetStatisticsAsync();

        Assert.AreEqual(100L, docCount);
        Assert.AreEqual(2048L, storageBytes);
    }
}
