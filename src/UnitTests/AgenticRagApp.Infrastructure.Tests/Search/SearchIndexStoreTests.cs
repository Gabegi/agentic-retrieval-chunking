using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Search;

namespace RagApp.UnitTests.Infrastructure.Search;

[TestClass]
public class SearchIndexStoreTests
{
    private static (SearchIndexStore Store, Mock<SearchIndexClient> Client) BuildStore()
    {
        var client = new Mock<SearchIndexClient>();
        var store  = new SearchIndexStore(client.Object);
        return (store, client);
    }

    [TestMethod]
    public async Task EnsureIndexAsync_IndexAlreadyExists_DoesNotCreateOrUpdate_ReturnsFalse()
    {
        var (store, client) = BuildStore();
        client.Setup(c => c.GetIndexAsync("my-index", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(new SearchIndex("my-index"), Mock.Of<Response>()));

        var created = await store.EnsureIndexAsync(new SearchIndex("my-index"));

        Assert.IsFalse(created);
        client.Verify(c => c.CreateOrUpdateIndexAsync(
            It.IsAny<SearchIndex>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task EnsureIndexAsync_IndexMissing_CreatesIt_ReturnsTrue()
    {
        var (store, client) = BuildStore();
        client.Setup(c => c.GetIndexAsync("my-index", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "not found"));
        client.Setup(c => c.CreateOrUpdateIndexAsync(
                It.IsAny<SearchIndex>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(new SearchIndex("my-index"), Mock.Of<Response>()));

        var created = await store.EnsureIndexAsync(new SearchIndex("my-index"));

        Assert.IsTrue(created);
        client.Verify(c => c.CreateOrUpdateIndexAsync(
            It.Is<SearchIndex>(i => i.Name == "my-index"), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task EnsureIndexAsync_UnexpectedFailureCheckingExistence_PropagatesWithoutCreating()
    {
        var (store, client) = BuildStore();
        client.Setup(c => c.GetIndexAsync("my-index", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(500, "server error"));

        await Assert.ThrowsExceptionAsync<RequestFailedException>(() => store.EnsureIndexAsync(new SearchIndex("my-index")));

        client.Verify(c => c.CreateOrUpdateIndexAsync(
            It.IsAny<SearchIndex>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task GetStatisticsAsync_ReturnsDocumentCountAndStorageSize()
    {
        var (store, client) = BuildStore();
        client.Setup(c => c.GetIndexStatisticsAsync("my-index", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(SearchModelFactory.SearchIndexStatistics(100, 2048), Mock.Of<Response>()));

        var (docCount, storageBytes) = await store.GetStatisticsAsync("my-index");

        Assert.AreEqual(100L, docCount);
        Assert.AreEqual(2048L, storageBytes);
    }
}
