using Azure;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Indexing.Pdf.Services;
using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.PdfExtraction;

[TestClass]
public class PdfExtractionPipelineTests
{
    private const string StateBlobName = "pdf-extraction-state.json";

    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        new Dictionary<string, string>();

    private static PDFExtractionResult SuccessResult(string blobName) => new(
        Ok: true, BlobName: blobName, FileSizeBytes: 100, PdfSpecVersion: 1.7,
        NativeMetadata: null, RawContent: "content",
        Pages: [new PdfPageRecord { BlobName = blobName, PageNumber = 1, PageContent = "content", Title = "Title" }],
        Structure: null, EstimatedCostUsd: 0.01m, Error: null);

    private static PdfCleanResult OneRecordCleanResult(string blobName)
    {
        var result = new PdfCleanResult();
        result.AddRecord(new CleanedPdfPageRecord { BlobName = blobName, PageNumber = 1, PageContent = "content", Title = "Title" });
        return result;
    }

    private static Mock<IBlobStore> MockBlobStore(
        IReadOnlyList<(string Name, DateTimeOffset? LastModified, long? ContentLength, IReadOnlyDictionary<string, string> Metadata)> blobs,
        byte[]? pdfBytes = null)
    {
        var store = new Mock<IBlobStore>();

        store.Setup(s => s.ListBlobsAsync(It.IsAny<BlobContainerClient>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(blobs);

        store.Setup(s => s.DownloadBytesAsync(It.IsAny<BlobContainerClient>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pdfBytes ?? [1, 2, 3]);

        // PdfExtractionPipeline.RunState is a private nested record, so it can't be named
        // here - Moq's automatic Task<T> handling for unconfigured members returns
        // Task.FromResult(default(T)) for these generic calls, which is exactly the
        // "no previous baseline" / "save succeeded" shape the pipeline already tolerates.

        return store;
    }

    private static Mock<IRunReportWriter> MockReportWriter(bool isEnabled = true)
    {
        var writer = new Mock<IRunReportWriter>();
        writer.SetupGet(w => w.IsEnabled).Returns(isEnabled);
        writer.Setup(w => w.WriteReportAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return writer;
    }

    private static Mock<IHostEnvironment> MockEnvironmentImpl(string environmentName)
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(environmentName);
        return env;
    }

    private static PdfExtractionPipeline BuildPipeline(
        Mock<IBlobStore> blobStore, Mock<IRunReportWriter> reportWriter,
        Mock<IPdfExtractor> extractor, Mock<IPdfCleaner> cleaner, Mock<IPdfPipelineValidator> validator,
        Mock<IHostEnvironment> env) =>
        new(
            new Mock<BlobContainerClient>().Object, new Mock<BlobContainerClient>().Object,
            blobStore.Object, reportWriter.Object, extractor.Object, cleaner.Object, validator.Object,
            env.Object, NullLogger<PdfExtractionPipeline>.Instance);

    private static Mock<IPdfExtractor> MockExtractor(params string[] blobNames)
    {
        var extractor = new Mock<IPdfExtractor>();
        foreach (var name in blobNames)
            extractor.Setup(e => e.ExtractPDFAsync(name, It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(SuccessResult(name));
        return extractor;
    }

    [TestMethod]
    public async Task HappyPath_ReturnsDocs()
    {
        var blobs = new[] { ("doc1.pdf", (DateTimeOffset?)DateTimeOffset.UtcNow, (long?)100, EmptyMetadata) };
        var blobStore    = MockBlobStore(blobs);
        var reportWriter = MockReportWriter();
        var extractor    = MockExtractor("doc1.pdf");
        var cleaner      = new Mock<IPdfCleaner>();
        cleaner.Setup(c => c.CleanPdf(It.IsAny<IReadOnlyList<PdfPageRecord>>())).Returns(OneRecordCleanResult("doc1.pdf"));
        var validator = new Mock<IPdfPipelineValidator>();
        validator.Setup(v => v.Validate(It.IsAny<IReadOnlyList<PDFExtractionResult>>(), It.IsAny<PdfCleanResult>(), It.IsAny<int?>(), It.IsAny<IReadOnlyList<PdfExtractionDiagnostics>?>()))
            .Returns(new PdfValidationReport { Passed = true, CleanedRecords = 1 });
        var env = MockEnvironmentImpl("Production");

        var pipeline = BuildPipeline(blobStore, reportWriter, extractor, cleaner, validator, env);

        var output = await pipeline.ExtractDocumentsAsync(new HashSet<string> { "doc1.pdf" });

        Assert.AreEqual(1, output.Docs.Count);
        Assert.AreEqual("doc1.pdf", output.Docs[0].SourceId);
    }

    [TestMethod]
    public async Task NonPdfBlob_NeverExtracted()
    {
        var blobs = new[]
        {
            ("doc1.pdf", (DateTimeOffset?)DateTimeOffset.UtcNow, (long?)100, EmptyMetadata),
            ("notes.txt", (DateTimeOffset?)DateTimeOffset.UtcNow, (long?)50, EmptyMetadata),
        };
        var blobStore    = MockBlobStore(blobs);
        var reportWriter = MockReportWriter();
        var extractor    = MockExtractor("doc1.pdf");
        var cleaner      = new Mock<IPdfCleaner>();
        cleaner.Setup(c => c.CleanPdf(It.IsAny<IReadOnlyList<PdfPageRecord>>())).Returns(OneRecordCleanResult("doc1.pdf"));
        var validator = new Mock<IPdfPipelineValidator>();
        validator.Setup(v => v.Validate(It.IsAny<IReadOnlyList<PDFExtractionResult>>(), It.IsAny<PdfCleanResult>(), It.IsAny<int?>(), It.IsAny<IReadOnlyList<PdfExtractionDiagnostics>?>()))
            .Returns(new PdfValidationReport { Passed = true, CleanedRecords = 1 });
        var env = MockEnvironmentImpl("Production");

        var pipeline = BuildPipeline(blobStore, reportWriter, extractor, cleaner, validator, env);

        await pipeline.ExtractDocumentsAsync(new HashSet<string> { "doc1.pdf", "notes.txt" });

        extractor.Verify(e => e.ExtractPDFAsync("notes.txt", It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task BlobNotInSourceIdsToProcess_NeverExtracted()
    {
        var blobs = new[] { ("doc1.pdf", (DateTimeOffset?)DateTimeOffset.UtcNow, (long?)100, EmptyMetadata) };
        var blobStore    = MockBlobStore(blobs);
        var reportWriter = MockReportWriter();
        var extractor    = MockExtractor("doc1.pdf");
        var cleaner      = new Mock<IPdfCleaner>();
        cleaner.Setup(c => c.CleanPdf(It.IsAny<IReadOnlyList<PdfPageRecord>>())).Returns(new PdfCleanResult());
        var validator = new Mock<IPdfPipelineValidator>();
        validator.Setup(v => v.Validate(It.IsAny<IReadOnlyList<PDFExtractionResult>>(), It.IsAny<PdfCleanResult>(), It.IsAny<int?>(), It.IsAny<IReadOnlyList<PdfExtractionDiagnostics>?>()))
            .Returns(new PdfValidationReport { Passed = true, CleanedRecords = 0 });
        var env = MockEnvironmentImpl("Production");

        var pipeline = BuildPipeline(blobStore, reportWriter, extractor, cleaner, validator, env);

        // sourceIdsToProcess deliberately excludes doc1.pdf - already up to date per the caller's diff.
        await pipeline.ExtractDocumentsAsync(new HashSet<string>());

        extractor.Verify(e => e.ExtractPDFAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task ValidationFailed_NotDevelopment_Throws()
    {
        var blobs = new[] { ("doc1.pdf", (DateTimeOffset?)DateTimeOffset.UtcNow, (long?)100, EmptyMetadata) };
        var blobStore    = MockBlobStore(blobs);
        var reportWriter = MockReportWriter();
        var extractor    = MockExtractor("doc1.pdf");
        var cleaner      = new Mock<IPdfCleaner>();
        cleaner.Setup(c => c.CleanPdf(It.IsAny<IReadOnlyList<PdfPageRecord>>())).Returns(OneRecordCleanResult("doc1.pdf"));
        var validator = new Mock<IPdfPipelineValidator>();
        validator.Setup(v => v.Validate(It.IsAny<IReadOnlyList<PDFExtractionResult>>(), It.IsAny<PdfCleanResult>(), It.IsAny<int?>(), It.IsAny<IReadOnlyList<PdfExtractionDiagnostics>?>()))
            .Returns(new PdfValidationReport { Passed = false, ReconciliationProblems = ["mismatch"] });
        var env = MockEnvironmentImpl("Production");

        var pipeline = BuildPipeline(blobStore, reportWriter, extractor, cleaner, validator, env);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => pipeline.ExtractDocumentsAsync(new HashSet<string> { "doc1.pdf" }));
    }

    [TestMethod]
    public async Task ValidationFailed_Development_ContinuesAndReturnsDocs()
    {
        var blobs = new[] { ("doc1.pdf", (DateTimeOffset?)DateTimeOffset.UtcNow, (long?)100, EmptyMetadata) };
        var blobStore    = MockBlobStore(blobs);
        var reportWriter = MockReportWriter();
        var extractor    = MockExtractor("doc1.pdf");
        var cleaner      = new Mock<IPdfCleaner>();
        cleaner.Setup(c => c.CleanPdf(It.IsAny<IReadOnlyList<PdfPageRecord>>())).Returns(OneRecordCleanResult("doc1.pdf"));
        var validator = new Mock<IPdfPipelineValidator>();
        validator.Setup(v => v.Validate(It.IsAny<IReadOnlyList<PDFExtractionResult>>(), It.IsAny<PdfCleanResult>(), It.IsAny<int?>(), It.IsAny<IReadOnlyList<PdfExtractionDiagnostics>?>()))
            .Returns(new PdfValidationReport { Passed = false, ReconciliationProblems = ["mismatch"], CleanedRecords = 1 });
        var env = MockEnvironmentImpl("Development");

        var pipeline = BuildPipeline(blobStore, reportWriter, extractor, cleaner, validator, env);

        var output = await pipeline.ExtractDocumentsAsync(new HashSet<string> { "doc1.pdf" });

        Assert.AreEqual(1, output.Docs.Count);
    }

    [TestMethod]
    public async Task ReportWriterDisabled_NoReportBlobsWritten()
    {
        var blobs = new[] { ("doc1.pdf", (DateTimeOffset?)DateTimeOffset.UtcNow, (long?)100, EmptyMetadata) };
        var blobStore    = MockBlobStore(blobs);
        var reportWriter = MockReportWriter(isEnabled: false);
        var extractor    = MockExtractor("doc1.pdf");
        var cleaner      = new Mock<IPdfCleaner>();
        cleaner.Setup(c => c.CleanPdf(It.IsAny<IReadOnlyList<PdfPageRecord>>())).Returns(OneRecordCleanResult("doc1.pdf"));
        var validator = new Mock<IPdfPipelineValidator>();
        validator.Setup(v => v.Validate(It.IsAny<IReadOnlyList<PDFExtractionResult>>(), It.IsAny<PdfCleanResult>(), It.IsAny<int?>(), It.IsAny<IReadOnlyList<PdfExtractionDiagnostics>?>()))
            .Returns(new PdfValidationReport { Passed = true, CleanedRecords = 1 });
        var env = MockEnvironmentImpl("Production");

        var pipeline = BuildPipeline(blobStore, reportWriter, extractor, cleaner, validator, env);

        await pipeline.ExtractDocumentsAsync(new HashSet<string> { "doc1.pdf" });

        reportWriter.Verify(w => w.WriteReportAsync(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task ExtractorThrowsForOneBlob_RecordedAsFileLevelError_RunStillSucceeds()
    {
        var blobs = new[] { ("doc1.pdf", (DateTimeOffset?)DateTimeOffset.UtcNow, (long?)100, EmptyMetadata) };
        var blobStore    = MockBlobStore(blobs);
        var reportWriter = MockReportWriter();
        var extractor    = new Mock<IPdfExtractor>();
        extractor.Setup(e => e.ExtractPDFAsync("doc1.pdf", It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var cleaner = new Mock<IPdfCleaner>();
        cleaner.Setup(c => c.CleanPdf(It.IsAny<IReadOnlyList<PdfPageRecord>>())).Returns(new PdfCleanResult());
        var validator = new Mock<IPdfPipelineValidator>();
        validator.Setup(v => v.Validate(It.IsAny<IReadOnlyList<PDFExtractionResult>>(), It.IsAny<PdfCleanResult>(), It.IsAny<int?>(), It.IsAny<IReadOnlyList<PdfExtractionDiagnostics>?>()))
            .Returns(new PdfValidationReport { Passed = true, CleanedRecords = 0 });
        var env = MockEnvironmentImpl("Production");

        var pipeline = BuildPipeline(blobStore, reportWriter, extractor, cleaner, validator, env);

        // A per-file exception must not abort the whole run - it becomes a file-level error instead.
        var output = await pipeline.ExtractDocumentsAsync(new HashSet<string> { "doc1.pdf" });

        Assert.AreEqual(0, output.Docs.Count);
    }
}
