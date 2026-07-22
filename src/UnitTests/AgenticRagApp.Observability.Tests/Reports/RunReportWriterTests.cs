using Azure;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Hosting;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Blob;
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

    private static Mock<IBlobStore> MockBlobStore() => new();

    private static RunReportWriter BuildWriter(Mock<IBlobStore> blobStore, bool isDevelopment = true) =>
        new(blobStore.Object, new Mock<BlobContainerClient>().Object, MockEnvironment(isDevelopment).Object);

    [TestMethod]
    public void IsEnabled_TrueInDevelopment()
    {
        var writer = BuildWriter(MockBlobStore(), isDevelopment: true);

        Assert.IsTrue(writer.IsEnabled);
    }

    [TestMethod]
    public void IsEnabled_FalseOutsideDevelopment()
    {
        var writer = BuildWriter(MockBlobStore(), isDevelopment: false);

        Assert.IsFalse(writer.IsEnabled);
    }

    [TestMethod]
    public async Task WriteReportAsync_UploadsSerializedReportToTheGivenPath()
    {
        var blobStore = MockBlobStore();
        var writer    = BuildWriter(blobStore);

        await writer.WriteReportAsync("some/path.json", new { Foo = "bar" });

        blobStore.Verify(s => s.UploadAsync(
            It.IsAny<BlobContainerClient>(), "some/path.json", It.IsAny<BinaryData>(), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task GetLastIndexStatsAsync_NoBaselineBlobYet_ReturnsNullRatherThanThrowing()
    {
        // No IBlobStore setup at all - Moq's default for an unconfigured generic call
        // returns default(T) (a null value tuple), mirroring "no baseline blob exists yet".
        var writer = BuildWriter(MockBlobStore());

        var stats = await writer.GetLastIndexStatsAsync("pdf");

        Assert.IsNull(stats);
    }

    [TestMethod]
    public async Task SaveLastIndexStatsAsync_UploadsToTheSourceScopedPath()
    {
        var blobStore = MockBlobStore();
        var writer    = BuildWriter(blobStore);

        await writer.SaveLastIndexStatsAsync("pdf", 100, 2048);

        blobStore.Verify(s => s.UploadAsync(
            It.IsAny<BlobContainerClient>(), "indexing/_last-stats-pdf.json", It.IsAny<BinaryData>(), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task SaveLastIndexStatsAsync_ScopesPathPerSource_PdfAndCsvNeverShareABaseline()
    {
        var blobStore = MockBlobStore();
        var writer    = BuildWriter(blobStore);

        await writer.SaveLastIndexStatsAsync("pdf", 100, 2048);
        await writer.SaveLastIndexStatsAsync("csv", 50, 1024);

        blobStore.Verify(s => s.UploadAsync(
            It.IsAny<BlobContainerClient>(), "indexing/_last-stats-pdf.json", It.IsAny<BinaryData>(), true, It.IsAny<CancellationToken>()), Times.Once);
        blobStore.Verify(s => s.UploadAsync(
            It.IsAny<BlobContainerClient>(), "indexing/_last-stats-csv.json", It.IsAny<BinaryData>(), true, It.IsAny<CancellationToken>()), Times.Once);
    }
}
