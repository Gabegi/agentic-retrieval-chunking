using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Search;

namespace RagApp.UnitTests.Infrastructure.Search;

[TestClass]
public class SearchDocumentStoreTests
{
    private static (SearchDocumentStore Store, Mock<SearchClient> Client) BuildStore()
    {
        var client = new Mock<SearchClient>();
        var store  = new SearchDocumentStore(client.Object, NullLogger<SearchDocumentStore>.Instance);
        return (store, client);
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
        var (store, client) = BuildStore();
        client.Setup(c => c.UploadDocumentsAsync(It.IsAny<IEnumerable<object>>(), It.IsAny<IndexDocumentsOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UploadResponse(("c1", true), ("c2", false)));

        var (succeeded, failed, batches) = await store.UpsertDocumentsAsync(new object[] { new { Id = "c1" }, new { Id = "c2" } });

        Assert.AreEqual(1, succeeded);
        Assert.AreEqual(1, failed);
        Assert.AreEqual(1, batches);
    }

    [TestMethod]
    public async Task GetCurrentIndexedDocumentDatesAsync_DeduplicatesByDocumentId_KeepingFirstOccurrence()
    {
        var (store, client) = BuildStore();
        var first  = DateTimeOffset.Parse("2024-01-01T00:00:00Z");
        var second = DateTimeOffset.Parse("2024-06-01T00:00:00Z");
        client.Setup(c => c.SearchAsync<SearchDocument>(It.IsAny<string>(), It.IsAny<SearchOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SearchResponse(DateDoc("doc1", first), DateDoc("doc1", second)));

        var result = await store.GetCurrentIndexedDocumentDatesAsync();

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(first, result["doc1"]);
    }

    [TestMethod]
    public async Task GetCurrentIndexedDocumentDatesAsync_KeyLookupIsCaseInsensitive()
    {
        var (store, client) = BuildStore();
        client.Setup(c => c.SearchAsync<SearchDocument>(It.IsAny<string>(), It.IsAny<SearchOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SearchResponse(DateDoc("DOC1", DateTimeOffset.UtcNow)));

        var result = await store.GetCurrentIndexedDocumentDatesAsync();

        Assert.IsTrue(result.ContainsKey("doc1"));
    }

    [TestMethod]
    public async Task GetChunkIdsForDocumentsAsync_NoDocumentIds_ReturnsEmptyWithoutSearching()
    {
        var (store, client) = BuildStore();

        var result = await store.GetChunkIdsForDocumentsAsync([]);

        Assert.AreEqual(0, result.Count);
        client.Verify(c => c.SearchAsync<SearchDocument>(It.IsAny<string>(), It.IsAny<SearchOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task GetChunkIdsForDocumentsAsync_ReturnsIdsFromSearchResults()
    {
        var (store, client) = BuildStore();
        client.Setup(c => c.SearchAsync<SearchDocument>(It.IsAny<string>(), It.IsAny<SearchOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SearchResponse(IdDoc("chunk1"), IdDoc("chunk2")));

        var result = await store.GetChunkIdsForDocumentsAsync(["doc1"]);

        CollectionAssert.AreEquivalent(new[] { "chunk1", "chunk2" }, result.ToList());
    }

    [TestMethod]
    public async Task DeleteChunksByIdAsync_NoIds_ReturnsZeroWithoutCallingClient()
    {
        var (store, client) = BuildStore();

        var result = await store.DeleteChunksByIdAsync([]);

        Assert.AreEqual(0, result);
        client.Verify(c => c.IndexDocumentsAsync(It.IsAny<IndexDocumentsBatch<object>>(), It.IsAny<IndexDocumentsOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task DeleteChunksByIdAsync_ReturnsCountOfIdsRequested()
    {
        var (store, client) = BuildStore();
        client.Setup(c => c.IndexDocumentsAsync(It.IsAny<IndexDocumentsBatch<object>>(), It.IsAny<IndexDocumentsOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UploadResponse(("c1", true), ("c2", true)));

        var result = await store.DeleteChunksByIdAsync(["c1", "c2"]);

        Assert.AreEqual(2, result);
    }
}
