using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using CsvIndexing.Services;
using IndexingShared.Observability.Reports;

namespace RagApp.UnitTests.CsvExtraction;

[TestClass]
public class CsvExtractionOrchestratorTests
{
    private const string PagesBlobName = "zenya_pages.csv";
    private const string IndexBlobName = "zenya_index.csv";
    private const string StateBlobName = "csv-extraction-state.json";

    private const string PagesHeader =
        "DOCUMENT_ID,TITLE,QUICK_CODE,FOLDER_MINI_FULL_PATH,LAST_MODIFIED_DATETIME,PAGE_INDEX,PAGE_CONTENT,RELATIVE_PATH,LANGUAGE";
    private const string IndexHeader =
        "DOCUMENT_ID,DOCUMENT_TYPE_NAME,SUMMARY,VERSION,REVISION,CHECK_DATE,ATTENTION_REQUIRED_FLAGS,ACTIVE";

    // One matched, active document/page - a clean run with nothing to complain about.
    private const string OnePageCsv  = PagesHeader + "\n" + "doc1,Title,QC,Folder,20240101120000,0,# Heading\\nSome content,rel,nl-NL\n";
    private const string OneIndexCsv = IndexHeader + "\n" + "doc1,Protocol,Summary,7,0,,[],true\n";

    // Same document, but marked inactive - joins to zero pages, so cleanResult ends up
    // empty even though every row parses fine (exercises the zero-cleaned-records guard).
    private const string InactiveIndexCsv = IndexHeader + "\n" + "doc1,Protocol,Summary,7,0,,[],false\n";

    private static Stream ToStream(string csv) => new MemoryStream(Encoding.UTF8.GetBytes(csv));

    private static Mock<BlobClient> MockBlobClient()
    {
        var blob = new Mock<BlobClient>();
        return blob;
    }

    // Wires up GetBlobClient(name) on a container mock to hand back the given BlobClient mock.
    private static Mock<BlobContainerClient> MockContainer(params (string Name, Mock<BlobClient> Client)[] blobs)
    {
        var container = new Mock<BlobContainerClient>();
        foreach (var (name, client) in blobs)
            container.Setup(c => c.GetBlobClient(name)).Returns(client.Object);
        return container;
    }

    // Source container serving pages.csv/index.csv as readable streams, exactly like a real
    // BlobClient.OpenReadAsync would once the download completes.
    private static BlobContainerClient BuildSourceContainer(string pagesCsv, string indexCsv)
    {
        var pagesBlob = MockBlobClient();
        pagesBlob.Setup(b => b.OpenReadAsync(It.IsAny<long>(), It.IsAny<int?>(), It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ToStream(pagesCsv));

        var indexBlob = MockBlobClient();
        indexBlob.Setup(b => b.OpenReadAsync(It.IsAny<long>(), It.IsAny<int?>(), It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ToStream(indexCsv));

        return MockContainer((PagesBlobName, pagesBlob), (IndexBlobName, indexBlob)).Object;
    }

    // State container backing the run-state blob (previous cleaned-record baseline).
    // existingContent == null means "blob doesn't exist yet" (first run).
    private static (BlobContainerClient Container, Mock<BlobClient> StateBlob) BuildStateContainer(
        string? existingContent, ETag? existingETag = null)
    {
        var stateBlob = MockBlobClient();

        stateBlob.Setup(b => b.ExistsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(existingContent is not null, Mock.Of<Response>()));

        if (existingContent is not null)
        {
            var details = BlobsModelFactory.BlobDownloadDetails(eTag: existingETag ?? new ETag("\"baseline\""));
            var result  = BlobsModelFactory.BlobDownloadResult(content: BinaryData.FromString(existingContent), details: details);
            stateBlob.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Response.FromValue(result, Mock.Of<Response>()));
        }

        stateBlob.Setup(b => b.UploadAsync(It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Response<BlobContentInfo>)null!);

        var container = MockContainer((StateBlobName, stateBlob));
        return (container.Object, stateBlob);
    }

    private static Mock<IRunReportWriter> MockReportWriter(bool isEnabled = true)
    {
        var writer = new Mock<IRunReportWriter>();
        writer.SetupGet(w => w.IsEnabled).Returns(isEnabled);
        writer.Setup(w => w.WriteReportAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return writer;
    }

    private static CsvExtractionOrchestrator BuildOrchestrator(
        BlobContainerClient container, BlobContainerClient stateContainer, IRunReportWriter reportWriter) =>
        new(
            container, stateContainer, reportWriter,
            new CsvExtractor(NullLogger<CsvExtractor>.Instance),
            new CsvJoiner(),
            new DataCleaner(),
            new PipelineValidator(),
            NullLogger<CsvExtractionOrchestrator>.Instance);

    [TestMethod]
    public async Task HappyPath_ReturnsDocsAndSavesStateUnconditionally()
    {
        var container = BuildSourceContainer(OnePageCsv, OneIndexCsv);
        var (stateContainer, stateBlob) = BuildStateContainer(existingContent: null); // first-ever run
        var reportWriter = MockReportWriter();
        var orchestrator = BuildOrchestrator(container, stateContainer, reportWriter.Object);

        var output = await orchestrator.ExtractDocumentsAsync();

        Assert.AreEqual(1, output.Docs.Count);
        Assert.AreEqual("doc1", output.Docs[0].SourceId);
        Assert.AreEqual(0, output.ValidationErrors);

        stateBlob.Verify(b => b.UploadAsync(
            It.IsAny<BinaryData>(),
            It.Is<BlobUploadOptions>(o => o.Conditions!.IfNoneMatch == ETag.All),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ZeroCleanedRecords_ThrowsAndNeverSavesState()
    {
        // Every row parses, but the sole document is inactive -> zero cleaned records ->
        // the reconciliation hard-gate should fail the run before state is ever saved.
        var container = BuildSourceContainer(OnePageCsv, InactiveIndexCsv);
        var (stateContainer, stateBlob) = BuildStateContainer(existingContent: null);
        var reportWriter = MockReportWriter();
        var orchestrator = BuildOrchestrator(container, stateContainer, reportWriter.Object);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => orchestrator.ExtractDocumentsAsync());

        stateBlob.Verify(b => b.UploadAsync(
            It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task MagnitudeShiftWithoutOverride_Throws()
    {
        // Previous run had 100 cleaned records; this run only has 1 - a -99% shift, well
        // past the 20% threshold PipelineValidator enforces.
        var container = BuildSourceContainer(OnePageCsv, OneIndexCsv);
        var (stateContainer, stateBlob) = BuildStateContainer(existingContent: "{\"CleanedRecords\":100}");
        var reportWriter = MockReportWriter();
        var orchestrator = BuildOrchestrator(container, stateContainer, reportWriter.Object);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => orchestrator.ExtractDocumentsAsync(overrideMagnitudeCheck: false));

        stateBlob.Verify(b => b.UploadAsync(
            It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task MagnitudeShiftWithOverride_ProceedsAndSavesStateConditionally()
    {
        var etag = new ETag("\"baseline\"");
        var container = BuildSourceContainer(OnePageCsv, OneIndexCsv);
        var (stateContainer, stateBlob) = BuildStateContainer(existingContent: "{\"CleanedRecords\":100}", existingETag: etag);
        var reportWriter = MockReportWriter();
        var orchestrator = BuildOrchestrator(container, stateContainer, reportWriter.Object);

        var output = await orchestrator.ExtractDocumentsAsync(overrideMagnitudeCheck: true);

        Assert.AreEqual(1, output.Docs.Count);

        // A conditional write against the baseline's own ETag, not an unconditional
        // "only if the blob doesn't exist yet" write - this run had a real baseline to race against.
        stateBlob.Verify(b => b.UploadAsync(
            It.IsAny<BinaryData>(),
            It.Is<BlobUploadOptions>(o => o.Conditions!.IfMatch == etag),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task CorruptStateBlob_TreatedAsNoBaselineAndRunStillSucceeds()
    {
        var container = BuildSourceContainer(OnePageCsv, OneIndexCsv);
        var (stateContainer, _) = BuildStateContainer(existingContent: "not-json-at-all");
        var reportWriter = MockReportWriter();
        var orchestrator = BuildOrchestrator(container, stateContainer, reportWriter.Object);

        var output = await orchestrator.ExtractDocumentsAsync();

        Assert.AreEqual(1, output.Docs.Count);
    }

    [TestMethod]
    public async Task PagesParseError_WritesSingleValidationReportBlob()
    {
        var badPagesCsv = PagesHeader + "\n" + "doc1,Title,QC,Folder,20240101120000,not-a-number,Content,rel,nl-NL\n";
        var container = BuildSourceContainer(badPagesCsv, OneIndexCsv);
        var (stateContainer, _) = BuildStateContainer(existingContent: null);
        var reportWriter = MockReportWriter();
        var orchestrator = BuildOrchestrator(container, stateContainer, reportWriter.Object);

        // The page fails to parse, and doc1's index record then has zero matching pages,
        // which also fails validation (RowsAttempted denominator makes this a 100% error rate) -
        // that's fine, this test only cares that the one consolidated report blob got written.
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => orchestrator.ExtractDocumentsAsync());

        reportWriter.Verify(w => w.WriteReportAsync(
            It.Is<string>(p => p.Contains("validation-report")), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task ReportWriterDisabled_NoBlobsWritten()
    {
        var badPagesCsv = PagesHeader + "\n" + "doc1,Title,QC,Folder,20240101120000,not-a-number,Content,rel,nl-NL\n";
        var container = BuildSourceContainer(badPagesCsv, OneIndexCsv);
        var (stateContainer, _) = BuildStateContainer(existingContent: null);
        var reportWriter = MockReportWriter(isEnabled: false);
        var orchestrator = BuildOrchestrator(container, stateContainer, reportWriter.Object);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => orchestrator.ExtractDocumentsAsync());

        reportWriter.Verify(w => w.WriteReportAsync(
            It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
