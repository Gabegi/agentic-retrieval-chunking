using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Infrastructure.Configuration;
using AgenticRagApp.Observability.Reports;
using AgenticRagApp.Services;

namespace RagApp.UnitTests.Indexing;

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

    private static (IndexDocumentService Service, Mock<ISearchDocumentStore> DocumentStore, Mock<ISearchIndexStore> IndexStore, Mock<IRunReportWriter> ReportWriter)
        BuildService()
    {
        var documentStore = new Mock<ISearchDocumentStore>();
        var indexStore    = new Mock<ISearchIndexStore>();
        var reportWriter  = new Mock<IRunReportWriter>();
        var service = new IndexDocumentService(Config(), documentStore.Object, indexStore.Object, reportWriter.Object, NullLogger<IndexDocumentService>.Instance);

        return (service, documentStore, indexStore, reportWriter);
    }

    [TestMethod]
    public async Task UpsertDocumentsAsync_MapsDocumentChunksAndDelegatesToStore()
    {
        var (service, documentStore, _, _) = BuildService();
        documentStore
            .Setup(s => s.UpsertDocumentsAsync(It.IsAny<IEnumerable<AgenticRagApp.Models.SearchUploadChunk>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((3, 1, 1));

        var (succeeded, failed) = await service.UpsertDocumentsAsync([
            new AgenticRagApp.Models.DocumentChunk { Id = "c1" },
            new AgenticRagApp.Models.DocumentChunk { Id = "c2" },
        ]);

        Assert.AreEqual(3, succeeded);
        Assert.AreEqual(1, failed);
    }

    [TestMethod]
    public async Task GetCurrentIndexedDocumentDatesAsync_DelegatesToStore()
    {
        var (service, documentStore, _, _) = BuildService();
        var expected = new Dictionary<string, DateTimeOffset> { ["doc1"] = DateTimeOffset.UtcNow };
        documentStore.Setup(s => s.GetCurrentIndexedDocumentDatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var result = await service.GetCurrentIndexedDocumentDatesAsync();

        Assert.AreSame(expected, result);
    }

    [TestMethod]
    public async Task GetStatisticsAsync_DelegatesToIndexStoreWithConfiguredIndexName()
    {
        var (service, _, indexStore, _) = BuildService();
        indexStore.Setup(s => s.GetStatisticsAsync("index", It.IsAny<CancellationToken>())).ReturnsAsync((100L, 2048L));

        var (docCount, storageBytes) = await service.GetStatisticsAsync();

        Assert.AreEqual(100L, docCount);
        Assert.AreEqual(2048L, storageBytes);
    }

    [TestMethod]
    public async Task CheckDriftAsync_NoBaseline_NoRedFlagsButStillSavesNewBaseline()
    {
        var (service, _, _, reportWriter) = BuildService();
        reportWriter.Setup(w => w.GetLastIndexStatsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(((long, long)?)null);

        var redFlags = await service.CheckDriftAsync(100, 2048);

        Assert.AreEqual(0, redFlags.Count);
        reportWriter.Verify(w => w.SaveLastIndexStatsAsync(100, 2048, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task CheckDriftAsync_WithinThreshold_NoRedFlags()
    {
        var (service, _, _, reportWriter) = BuildService();
        reportWriter.Setup(w => w.GetLastIndexStatsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(((long DocumentCount, long StorageSizeBytes)?)(100L, 1000L));

        // +10% - within the 15% threshold.
        var redFlags = await service.CheckDriftAsync(110, 1000);

        Assert.AreEqual(0, redFlags.Count);
    }

    [TestMethod]
    public async Task CheckDriftAsync_BeyondThreshold_FlagsDrift()
    {
        var (service, _, _, reportWriter) = BuildService();
        reportWriter.Setup(w => w.GetLastIndexStatsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(((long DocumentCount, long StorageSizeBytes)?)(100L, 1000L));

        // -50% - well beyond the 15% threshold.
        var redFlags = await service.CheckDriftAsync(50, 1000);

        Assert.AreEqual(1, redFlags.Count);
        Assert.IsTrue(redFlags[0].Contains("index_doc_count_drift"));
    }

    [TestMethod]
    public async Task CheckDriftAsync_ZeroBaselineDocumentCount_SkipsComparisonToAvoidDivideByZero()
    {
        var (service, _, _, reportWriter) = BuildService();
        reportWriter.Setup(w => w.GetLastIndexStatsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(((long DocumentCount, long StorageSizeBytes)?)(0L, 0L));

        var redFlags = await service.CheckDriftAsync(1000, 2048);

        Assert.AreEqual(0, redFlags.Count);
    }
}
