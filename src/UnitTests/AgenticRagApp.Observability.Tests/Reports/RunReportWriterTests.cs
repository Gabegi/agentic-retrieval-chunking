using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Hosting;
using Moq;
using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.Observability;

[TestClass]
public class RunReportWriterTests
{
    private static Mock<IHostEnvironment> MockEnvironment(bool isDevelopment)
    {
        var mock = new Mock<IHostEnvironment>();
        mock.SetupGet(e => e.EnvironmentName).Returns(isDevelopment ? Environments.Development : Environments.Production);
        return mock;
    }

    private static (Mock<BlobContainerClient> Container, Mock<BlobClient> Blob) MockContainer()
    {
        var blob = new Mock<BlobClient>();
        blob.Setup(b => b.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(
                BlobsModelFactory.BlobContentInfo(
                    eTag: new ETag("etag"), lastModified: DateTimeOffset.UtcNow, contentHash: [],
                    encryptionKeySha256: "", encryptionScope: "", blobSequenceNumber: 0, versionId: ""),
                Mock.Of<Response>()));

        var container = new Mock<BlobContainerClient>();
        container.Setup(c => c.CreateIfNotExistsAsync(
                It.IsAny<PublicAccessType>(), It.IsAny<IDictionary<string, string>>(), It.IsAny<BlobContainerEncryptionScopeOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Response<BlobContainerInfo>)null!);
        container.Setup(c => c.GetBlobClient(It.IsAny<string>())).Returns(blob.Object);

        return (container, blob);
    }

    [TestMethod]
    public void IsEnabled_TrueInDevelopment()
    {
        var (container, _) = MockContainer();
        var writer = new RunReportWriter(container.Object, MockEnvironment(isDevelopment: true).Object);

        Assert.IsTrue(writer.IsEnabled);
    }

    [TestMethod]
    public void IsEnabled_FalseOutsideDevelopment()
    {
        var (container, _) = MockContainer();
        var writer = new RunReportWriter(container.Object, MockEnvironment(isDevelopment: false).Object);

        Assert.IsFalse(writer.IsEnabled);
    }

    [TestMethod]
    public async Task WriteReportAsync_UploadsSerializedReportToTheGivenPath()
    {
        var (container, blob) = MockContainer();
        var writer = new RunReportWriter(container.Object, MockEnvironment(isDevelopment: true).Object);

        await writer.WriteReportAsync("some/path.json", new { Foo = "bar" });

        container.Verify(c => c.GetBlobClient("some/path.json"), Times.Once);
        blob.Verify(b => b.UploadAsync(It.IsAny<Stream>(), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task GetLastIndexStatsAsync_BlobDoesNotExist_ReturnsNull()
    {
        var (container, blob) = MockContainer();
        blob.Setup(b => b.ExistsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(false, Mock.Of<Response>()));
        var writer = new RunReportWriter(container.Object, MockEnvironment(isDevelopment: true).Object);

        var stats = await writer.GetLastIndexStatsAsync();

        Assert.IsNull(stats);
    }

    [TestMethod]
    public async Task GetLastIndexStatsAsync_BlobExists_ReturnsDeserializedStats()
    {
        var (container, blob) = MockContainer();
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { DocumentCount = 100L, StorageSizeBytes = 2048L });
        blob.Setup(b => b.ExistsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(true, Mock.Of<Response>()));
        blob.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(
                BlobsModelFactory.BlobDownloadResult(content: BinaryData.FromBytes(json)),
                Mock.Of<Response>()));
        var writer = new RunReportWriter(container.Object, MockEnvironment(isDevelopment: true).Object);

        var stats = await writer.GetLastIndexStatsAsync();

        Assert.IsNotNull(stats);
        Assert.AreEqual(100L, stats.Value.DocumentCount);
        Assert.AreEqual(2048L, stats.Value.StorageSizeBytes);
    }

    [TestMethod]
    public async Task GetLastIndexStatsAsync_CorruptBlob_ReturnsNullRatherThanThrowing()
    {
        var (container, blob) = MockContainer();
        blob.Setup(b => b.ExistsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(true, Mock.Of<Response>()));
        blob.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(500, "corrupt"));
        var writer = new RunReportWriter(container.Object, MockEnvironment(isDevelopment: true).Object);

        var stats = await writer.GetLastIndexStatsAsync();

        Assert.IsNull(stats);
    }

    [TestMethod]
    public async Task SaveLastIndexStatsAsync_UploadsToTheWellKnownPath()
    {
        var (container, blob) = MockContainer();
        var writer = new RunReportWriter(container.Object, MockEnvironment(isDevelopment: true).Object);

        await writer.SaveLastIndexStatsAsync(100, 2048);

        container.Verify(c => c.GetBlobClient("indexing/_last-stats.json"), Times.Once);
        blob.Verify(b => b.UploadAsync(It.IsAny<Stream>(), true, It.IsAny<CancellationToken>()), Times.Once);
    }
}
