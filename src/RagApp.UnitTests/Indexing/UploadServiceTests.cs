using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRag.Models;
using AgenticRag.Services;

namespace RagApp.UnitTests.Indexing;

[TestClass]
public class UploadServiceTests
{
    private static DocumentChunk Document(string id) => new() { Id = id, Content = "content" };

    private static Mock<IIndexDocumentService> MockIndexDocumentService(
        int succeeded, int failed,
        IReadOnlyList<string>? existingChunkIds = null,
        int deletedCount = 0,
        (long DocCount, long StorageBytes)? stats = null,
        IReadOnlyList<string>? driftRedFlags = null,
        Exception? statsException = null)
    {
        var mock = new Mock<IIndexDocumentService>();
        mock.Setup(m => m.UpsertDocumentsAsync(It.IsAny<IEnumerable<DocumentChunk>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((succeeded, failed));
        mock.Setup(m => m.GetChunkIdsForDocumentsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingChunkIds ?? []);
        mock.Setup(m => m.DeleteChunksByIdAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(deletedCount);

        if (statsException is not null)
            mock.Setup(m => m.GetStatisticsAsync(It.IsAny<CancellationToken>())).ThrowsAsync(statsException);
        else
            mock.Setup(m => m.GetStatisticsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(stats ?? (0L, 0L));

        mock.Setup(m => m.CheckDriftAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(driftRedFlags ?? []);

        return mock;
    }

    private static UploadService BuildService(Mock<IIndexDocumentService> indexDocumentService) =>
        new(indexDocumentService.Object, NullLogger<UploadService>.Instance);

    [TestMethod]
    public async Task UploadDocumentsAsync_ReturnsSucceededAndFailedCountsFromIndexService()
    {
        var indexService = MockIndexDocumentService(succeeded: 3, failed: 1);
        var service      = BuildService(indexService);

        var result = await service.UploadDocumentsAsync([Document("d1")], staleDocumentIds: []);

        Assert.AreEqual(3, result.DocsUploaded);
        Assert.AreEqual(1, result.DocsFailed);
    }

    [TestMethod]
    public async Task UploadDocumentsAsync_NoStaleDocuments_SkipsCleanupEntirely()
    {
        var indexService = MockIndexDocumentService(succeeded: 1, failed: 0);
        var service      = BuildService(indexService);

        var result = await service.UploadDocumentsAsync([Document("d1")], staleDocumentIds: []);

        Assert.AreEqual(0, result.ChunksRemoved);
        indexService.Verify(m => m.GetChunkIdsForDocumentsAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        indexService.Verify(m => m.DeleteChunksByIdAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task UploadDocumentsAsync_OrphanedChunks_AreDeleted()
    {
        // doc1's old chunk ids: c1 (re-uploaded, keep) and c2 (no longer produced, orphaned).
        var indexService = MockIndexDocumentService(
            succeeded: 1, failed: 0,
            existingChunkIds: ["c1", "c2"],
            deletedCount: 1);
        var service = BuildService(indexService);

        var result = await service.UploadDocumentsAsync([Document("c1")], staleDocumentIds: ["doc1"]);

        Assert.AreEqual(1, result.ChunksRemoved);
        indexService.Verify(m => m.DeleteChunksByIdAsync(
            It.Is<IEnumerable<string>>(ids => ids.SequenceEqual(new[] { "c2" })), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task UploadDocumentsAsync_AllOldChunksWereReuploaded_NoDeleteCallMade()
    {
        // Every previously-existing chunk id for the stale doc is among what was just uploaded.
        var indexService = MockIndexDocumentService(
            succeeded: 1, failed: 0,
            existingChunkIds: ["c1"]);
        var service = BuildService(indexService);

        var result = await service.UploadDocumentsAsync([Document("c1")], staleDocumentIds: ["doc1"]);

        Assert.AreEqual(0, result.ChunksRemoved);
        indexService.Verify(m => m.DeleteChunksByIdAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task UploadDocumentsAsync_StatsSnapshotSucceeds_PopulatesSnapshotAndRedFlags()
    {
        var indexService = MockIndexDocumentService(
            succeeded: 1, failed: 0,
            stats: (100L, 2048L),
            driftRedFlags: ["index_doc_count_drift:+50.0% (50 -> 100)"]);
        var service = BuildService(indexService);

        var result = await service.UploadDocumentsAsync([Document("d1")], staleDocumentIds: []);

        Assert.AreEqual(100L, result.IndexDocumentCountSnapshot);
        Assert.AreEqual(2048L, result.IndexStorageSizeBytesSnapshot);
        CollectionAssert.Contains(result.RedFlags.ToList(), "index_doc_count_drift:+50.0% (50 -> 100)");
    }

    [TestMethod]
    public async Task UploadDocumentsAsync_StatsSnapshotFails_UploadResultStillReturnedWithNullSnapshot()
    {
        var indexService = MockIndexDocumentService(
            succeeded: 5, failed: 0,
            statsException: new InvalidOperationException("search unavailable"));
        var service = BuildService(indexService);

        var result = await service.UploadDocumentsAsync([Document("d1")], staleDocumentIds: []);

        Assert.AreEqual(5, result.DocsUploaded);
        Assert.IsNull(result.IndexDocumentCountSnapshot);
        Assert.IsNull(result.IndexStorageSizeBytesSnapshot);
        Assert.AreEqual(0, result.RedFlags.Count);
    }

    [TestMethod]
    public async Task UploadDocumentsAsync_StatsSnapshotCancelled_ExceptionPropagates()
    {
        var indexService = MockIndexDocumentService(
            succeeded: 1, failed: 0,
            statsException: new OperationCanceledException());
        var service = BuildService(indexService);

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => service.UploadDocumentsAsync([Document("d1")], staleDocumentIds: []));
    }
}
