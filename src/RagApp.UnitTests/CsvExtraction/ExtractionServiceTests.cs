using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ProtocolsIndexer.Models;
using ProtocolsIndexer.Observability.Reports;
using ProtocolsIndexer.Services;

namespace RagApp.UnitTests.CsvExtraction;

[TestClass]
public class ExtractionServiceTests
{
    private static ExtractionDocument Doc(string sourceId, string lastModified = "2024-01-01") => new(
        SourceId: sourceId,
        Ordinal:  0,
        Content:  "content",
        Metadata: new Dictionary<string, string> { ["last_modified_date"] = lastModified });

    private static ExtractionOutput BuildOutput(params ExtractionDocument[] docs) => new(
        Docs:                   docs,
        ValidationErrors:       0,
        ValidationWarnings:     0,
        ReconciliationProblems: 0,
        StaleDocCount:          0,
        MojibakeRepairedPages:  0,
        DetectedTableCount:     0,
        DocsWithoutHeadings:    0,
        MissingTitleCount:      0,
        MissingVersionCount:    0,
        MissingDepartmentCount: 0,
        Issues:                 [],
        RedFlags:               [],
        SpotCheckSample:        []);

    private static Mock<IExtractionOrchestrator> MockExtractor(ExtractionOutput output, string source = "csv")
    {
        var mock = new Mock<IExtractionOrchestrator>();
        mock.SetupGet(m => m.Source).Returns(source);
        mock.Setup(m => m.ExtractDocumentsAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync(output);
        return mock;
    }

    // Mirrors the real IndexDocumentService.GetCurrentIndexedDocumentDatesAsync, which builds
    // this dictionary with an OrdinalIgnoreCase comparer - the case-insensitive SourceId
    // matching in ExtractionService relies on that, not on anything it configures itself.
    private static Mock<IIndexDocumentService> MockIndexService(Dictionary<string, DateTimeOffset> indexedDates)
    {
        var caseInsensitive = new Dictionary<string, DateTimeOffset>(indexedDates, StringComparer.OrdinalIgnoreCase);
        var mock = new Mock<IIndexDocumentService>();
        mock.Setup(m => m.GetCurrentIndexedDocumentDatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(caseInsensitive);
        return mock;
    }

    private static Mock<IRunReportWriter> MockReportWriter(bool isEnabled = true)
    {
        var writer = new Mock<IRunReportWriter>();
        writer.SetupGet(w => w.IsEnabled).Returns(isEnabled);
        writer.Setup(w => w.WriteReportAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return writer;
    }

    private static ExtractionService BuildService(
        Mock<IExtractionOrchestrator> extractor, Mock<IIndexDocumentService> indexService, Mock<IRunReportWriter> reportWriter) =>
        new(extractor.Object, indexService.Object, reportWriter.Object, NullLogger<ExtractionService>.Instance);

    [TestMethod]
    public async Task NewDocument_NotYetIndexed_IsCountedAsNewAndProcessed()
    {
        var extractor    = MockExtractor(BuildOutput(Doc("doc1")));
        var indexService = MockIndexService([]);
        var service      = BuildService(extractor, indexService, MockReportWriter(isEnabled: false));

        var (docs, stats) = await service.ExtractAsync(forceReindex: false);

        Assert.AreEqual(1, docs.Count);
        Assert.AreEqual("doc1", docs[0].SourceId);
        Assert.AreEqual("csv", stats.Source);
        Assert.AreEqual(1, stats.DocsNew);
        Assert.AreEqual(0, stats.DocsUpdated);
        Assert.AreEqual(0, stats.DocsSkipped);
        Assert.AreEqual(0, stats.DocsDeleted);
        Assert.AreEqual(0, stats.StaleDocumentIds.Count);
    }

    [TestMethod]
    public async Task UnmodifiedDocument_AlreadyIndexed_IsSkipped()
    {
        // Indexed AFTER the doc's last_modified_date - nothing new to do.
        var extractor    = MockExtractor(BuildOutput(Doc("doc1", lastModified: "2024-01-01")));
        var indexService = MockIndexService(new() { ["doc1"] = DateTimeOffset.Parse("2024-06-01") });
        var service      = BuildService(extractor, indexService, MockReportWriter(isEnabled: false));

        var (docs, stats) = await service.ExtractAsync(forceReindex: false);

        Assert.AreEqual(0, docs.Count);
        Assert.AreEqual(0, stats.DocsNew);
        Assert.AreEqual(0, stats.DocsUpdated);
        Assert.AreEqual(1, stats.DocsSkipped);
    }

    [TestMethod]
    public async Task ModifiedDocument_AlreadyIndexed_IsCountedAsUpdatedAndMarkedStale()
    {
        // Indexed BEFORE the doc's last_modified_date - stale, needs reprocessing.
        var extractor    = MockExtractor(BuildOutput(Doc("doc1", lastModified: "2024-06-01")));
        var indexService = MockIndexService(new() { ["doc1"] = DateTimeOffset.Parse("2024-01-01") });
        var service      = BuildService(extractor, indexService, MockReportWriter(isEnabled: false));

        var (docs, stats) = await service.ExtractAsync(forceReindex: false);

        Assert.AreEqual(1, docs.Count);
        Assert.AreEqual(0, stats.DocsNew);
        Assert.AreEqual(1, stats.DocsUpdated);
        Assert.AreEqual(0, stats.DocsSkipped);
        CollectionAssert.Contains(stats.StaleDocumentIds.ToList(), "doc1");
    }

    [TestMethod]
    public async Task ForceReindex_ReprocessesEvenAnUnmodifiedDocument()
    {
        var extractor    = MockExtractor(BuildOutput(Doc("doc1", lastModified: "2024-01-01")));
        var indexService = MockIndexService(new() { ["doc1"] = DateTimeOffset.Parse("2024-06-01") }); // would normally skip
        var service      = BuildService(extractor, indexService, MockReportWriter(isEnabled: false));

        var (docs, stats) = await service.ExtractAsync(forceReindex: true);

        Assert.AreEqual(1, docs.Count);
        Assert.AreEqual(1, stats.DocsUpdated);
        Assert.AreEqual(0, stats.DocsSkipped);
    }

    [TestMethod]
    public async Task DocumentRemovedFromSource_IsCountedAsDeletedAndMarkedStale()
    {
        // doc2 was previously indexed but the extractor no longer returns it - withdrawn upstream.
        var extractor    = MockExtractor(BuildOutput(Doc("doc1")));
        var indexService = MockIndexService(new() { ["doc2"] = DateTimeOffset.UtcNow });
        var service      = BuildService(extractor, indexService, MockReportWriter(isEnabled: false));

        var (docs, stats) = await service.ExtractAsync(forceReindex: false);

        Assert.AreEqual(1, docs.Count);
        Assert.AreEqual("doc1", docs[0].SourceId);
        Assert.AreEqual(1, stats.DocsNew);
        Assert.AreEqual(1, stats.DocsDeleted);
        CollectionAssert.Contains(stats.StaleDocumentIds.ToList(), "doc2");
    }

    [TestMethod]
    public async Task SourceIdMatching_IsCaseInsensitive()
    {
        // Extracted as "DOC1", indexed as "doc1" - same document, must not be treated as
        // both a new doc AND a removed one.
        var extractor    = MockExtractor(BuildOutput(Doc("DOC1", lastModified: "2024-01-01")));
        var indexService = MockIndexService(new() { ["doc1"] = DateTimeOffset.Parse("2024-06-01") });
        var service      = BuildService(extractor, indexService, MockReportWriter(isEnabled: false));

        var (_, stats) = await service.ExtractAsync(forceReindex: false);

        Assert.AreEqual(0, stats.DocsNew);
        Assert.AreEqual(0, stats.DocsDeleted);
        Assert.AreEqual(1, stats.DocsSkipped);
    }

    [TestMethod]
    public async Task Stats_PropagatesValidationFieldsFromExtractionOutput()
    {
        var output = new ExtractionOutput(
            Docs:                   [Doc("doc1")],
            ValidationErrors:       3,
            ValidationWarnings:     5,
            ReconciliationProblems: 1,
            StaleDocCount:          2,
            MojibakeRepairedPages:  6,
            DetectedTableCount:     7,
            DocsWithoutHeadings:    4,
            MissingTitleCount:      1,
            MissingVersionCount:    2,
            MissingDepartmentCount: 3,
            Issues:                 [],
            RedFlags:               ["some flag"],
            SpotCheckSample:        []);
        var extractor    = MockExtractor(output);
        var indexService = MockIndexService([]);
        var service      = BuildService(extractor, indexService, MockReportWriter(isEnabled: false));

        var (_, stats) = await service.ExtractAsync(forceReindex: false);

        Assert.AreEqual(3, stats.ValidationErrors);
        Assert.AreEqual(5, stats.ValidationWarnings);
        Assert.AreEqual(1, stats.ReconciliationProblems);
        Assert.AreEqual(2, stats.StaleDocCount);
        Assert.AreEqual(6, stats.MojibakeRepairedPages);
        Assert.AreEqual(7, stats.DetectedTableCount);
        Assert.AreEqual(4, stats.DocsWithoutHeadings);
        Assert.AreEqual(1, stats.MissingTitleCount);
        Assert.AreEqual(2, stats.MissingVersionCount);
        Assert.AreEqual(3, stats.MissingDepartmentCount);
        CollectionAssert.Contains(stats.RedFlags.ToList(), "some flag");
    }

    [TestMethod]
    public async Task ReportWriterEnabled_WritesDiffReportBlob()
    {
        var extractor    = MockExtractor(BuildOutput(Doc("doc1")));
        var indexService = MockIndexService([]);
        var reportWriter = MockReportWriter(isEnabled: true);
        var service      = BuildService(extractor, indexService, reportWriter);

        await service.ExtractAsync(forceReindex: false);

        reportWriter.Verify(w => w.WriteReportAsync(
            It.Is<string>(p => p.Contains("diff")), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ReportWriterDisabled_NoDiffReportWritten()
    {
        var extractor    = MockExtractor(BuildOutput(Doc("doc1")));
        var indexService = MockIndexService([]);
        var reportWriter = MockReportWriter(isEnabled: false);
        var service      = BuildService(extractor, indexService, reportWriter);

        await service.ExtractAsync(forceReindex: false);

        reportWriter.Verify(w => w.WriteReportAsync(
            It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
