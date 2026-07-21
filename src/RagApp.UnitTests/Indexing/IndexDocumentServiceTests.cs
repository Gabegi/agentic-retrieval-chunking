using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
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

    private static (IndexDocumentService Service, Mock<SearchClient> Search, Mock<SearchIndexClient> Index, Mock<IRunReportWriter> ReportWriter)
        BuildService()
    {
        var reportWriter = new Mock<IRunReportWriter>();
        var searchClient = new Mock<SearchClient>();
        var indexClient  = new Mock<SearchIndexClient>();
        var service = new IndexDocumentService(Config(), searchClient.Object, indexClient.Object, reportWriter.Object, NullLogger<IndexDocumentService>.Instance);

        return (service, searchClient, indexClient, reportWriter);
    }

    private static Response<IndexDocumentsResult> UploadResponse(params (string Key, bool Succeeded)[] results)
    {
        var indexingResults = results.Select(r => SearchModelFactory.IndexingResult(r.Key, r.Succeeded ? null : "failed", r.Succeeded, r.Succeeded ? 200 : 400)).ToList();
        return Response.FromValue(SearchModelFactory.IndexDocumentsResult(indexingResults), Mock.Of<Response>());
    }

    private static SearchDocument DateDoc(string documentId, DateTimeOffset lastModified) => new()
    {
        ["document_id"]        = documentId,
        ["last_modified_date"] = lastModified,
    };

    private static SearchDocument IdDoc(string id) => new() { ["id"] = id };

    private static Response<SearchResults<SearchDocument>> SearchResponse(params SearchDocument[] docs)
    {
        var results = SearchModelFactory.SearchResults(
            values: docs.Select(d => SearchModelFactory.SearchResult(d, 0.0, null)).ToList(),
            totalCount: (long)docs.Length,
            facets: null,
            coverage: null,
            rawResponse: Mock.Of<Response>());
        return Response.FromValue(results, Mock.Of<Response>());
    }

    [TestMethod]
    public async Task UpsertDocumentsAsync_CountsSucceededAndFailedFromResponse()
    {
        var (service, search, _, _) = BuildService();
        search.Setup(s => s.UploadDocumentsAsync(It.IsAny<IEnumerable<object>>(), It.IsAny<IndexDocumentsOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UploadResponse(("c1", true), ("c2", false)));

        var (succeeded, failed) = await service.UpsertDocumentsAsync([new AgenticRagApp.Models.DocumentChunk { Id = "c1" }, new AgenticRagApp.Models.DocumentChunk { Id = "c2" }]);

        Assert.AreEqual(1, succeeded);
        Assert.AreEqual(1, failed);
    }

    [TestMethod]
    public async Task GetCurrentIndexedDocumentDatesAsync_DeduplicatesByDocumentId_KeepingFirstOccurrence()
    {
        var (service, search, _, _) = BuildService();
        var first  = DateTimeOffset.Parse("2024-01-01T00:00:00Z");
        var second = DateTimeOffset.Parse("2024-06-01T00:00:00Z");
        search.Setup(s => s.SearchAsync<SearchDocument>(It.IsAny<string>(), It.IsAny<SearchOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SearchResponse(DateDoc("doc1", first), DateDoc("doc1", second)));

        var result = await service.GetCurrentIndexedDocumentDatesAsync();

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(first, result["doc1"]);
    }

    [TestMethod]
    public async Task GetCurrentIndexedDocumentDatesAsync_KeyLookupIsCaseInsensitive()
    {
        var (service, search, _, _) = BuildService();
        search.Setup(s => s.SearchAsync<SearchDocument>(It.IsAny<string>(), It.IsAny<SearchOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SearchResponse(DateDoc("DOC1", DateTimeOffset.UtcNow)));

        var result = await service.GetCurrentIndexedDocumentDatesAsync();

        Assert.IsTrue(result.ContainsKey("doc1"));
    }

    [TestMethod]
    public async Task GetChunkIdsForDocumentsAsync_NoDocumentIds_ReturnsEmptyWithoutSearching()
    {
        var (service, search, _, _) = BuildService();

        var result = await service.GetChunkIdsForDocumentsAsync([]);

        Assert.AreEqual(0, result.Count);
        search.Verify(s => s.SearchAsync<SearchDocument>(It.IsAny<string>(), It.IsAny<SearchOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task GetChunkIdsForDocumentsAsync_ReturnsIdsFromSearchResults()
    {
        var (service, search, _, _) = BuildService();
        search.Setup(s => s.SearchAsync<SearchDocument>(It.IsAny<string>(), It.IsAny<SearchOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SearchResponse(IdDoc("chunk1"), IdDoc("chunk2")));

        var result = await service.GetChunkIdsForDocumentsAsync(["doc1"]);

        CollectionAssert.AreEquivalent(new[] { "chunk1", "chunk2" }, result.ToList());
    }

    [TestMethod]
    public async Task DeleteChunksByIdAsync_NoIds_ReturnsZeroWithoutCallingSearchClient()
    {
        var (service, search, _, _) = BuildService();

        var result = await service.DeleteChunksByIdAsync([]);

        Assert.AreEqual(0, result);
        search.Verify(s => s.IndexDocumentsAsync(It.IsAny<IndexDocumentsBatch<object>>(), It.IsAny<IndexDocumentsOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task DeleteChunksByIdAsync_ReturnsCountOfIdsRequested()
    {
        var (service, search, _, _) = BuildService();
        search.Setup(s => s.IndexDocumentsAsync(It.IsAny<IndexDocumentsBatch<object>>(), It.IsAny<IndexDocumentsOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UploadResponse(("c1", true), ("c2", true)));

        var result = await service.DeleteChunksByIdAsync(["c1", "c2"]);

        Assert.AreEqual(2, result);
    }

    [TestMethod]
    public async Task GetStatisticsAsync_ReturnsDocumentCountAndStorageSize()
    {
        var (service, _, indexClient, _) = BuildService();
        indexClient.Setup(c => c.GetIndexStatisticsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(SearchModelFactory.SearchIndexStatistics(100, 2048), Mock.Of<Response>()));

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
