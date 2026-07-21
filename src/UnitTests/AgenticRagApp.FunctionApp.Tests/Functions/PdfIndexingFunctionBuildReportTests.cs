using System.Reflection;
using Microsoft.DurableTask;
using Moq;
using AgenticRagApp.Functions;
using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.Functions;

// PdfIndexingFunction.BuildReport is a private static method - the single biggest risk hotspot
// in the assembly (highest cyclomatic complexity, all null-coalescing over the three stage
// results). It's pure data assembly with no I/O, so it's invoked directly via reflection
// rather than exercising it indirectly through the orchestrator.
[TestClass]
public class PdfIndexingFunctionBuildReportTests
{
    private static readonly MethodInfo BuildReportMethod =
        typeof(PdfIndexingFunction).GetMethod("BuildReport", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static Mock<TaskOrchestrationContext> MockContext(string instanceId, DateTime finishedAt)
    {
        var mock = new Mock<TaskOrchestrationContext>();
        mock.SetupGet(c => c.InstanceId).Returns(instanceId);
        mock.SetupGet(c => c.CurrentUtcDateTime).Returns(finishedAt);
        return mock;
    }

    private static PdfIndexRunReport Invoke(
        TaskOrchestrationContext context, DateTimeOffset startedAt, IndexRequest input,
        ExtractionResults? ext, ChunkingResults? chunk, EmbedUploadingResults? embed,
        bool success, string? error) =>
        (PdfIndexRunReport)BuildReportMethod.Invoke(null, [context, startedAt, input, ext, chunk, embed, success, error])!;

    private static ExtractionResults Extraction(int docsNew = 1, IReadOnlyList<string>? staleIds = null, IReadOnlyList<string>? redFlags = null) => new(
        Source:                 "pdf",
        DocsToProcess:          1,
        DocsSkipped:            2,
        DocsNew:                docsNew,
        DocsUpdated:            3,
        DocsDeleted:            4,
        StaleDocumentIds:       staleIds ?? [],
        ValidationErrors:       5,
        ValidationWarnings:     6,
        ReconciliationProblems: 7,
        StaleDocCount:          null,
        MojibakeRepairedPages:  13,
        DetectedTableCount:     14,
        DocsWithoutHeadings:    9,
        MissingTitleCount:      10,
        MissingVersionCount:    null,
        MissingDepartmentCount: null,
        Issues:                 [new ValidationIssueEntry("Extract", "Error", "doc1", "bad row")],
        RedFlags:               redFlags ?? ["extraction flag"],
        SpotCheckSample:        [new SpotCheckEntry("doc1", "Title", "preview...")]);

    private static ChunkingResults Chunking() => new(
        ChunksProduced:     20,
        DocsWithZeroChunks: 1,
        DuplicateChunks:    2,
        MinChunkSizeChars:  50,
        MaxChunkSizeChars:  1500,
        AvgChunkSizeChars:  750.5,
        P95ChunkSizeChars:  1400,
        BandUnder100:       1,
        Band100To500:       2,
        Band500To1500:      15,
        Band1500Plus:       2,
        CoherentChunks:     18,
        HeadingsDetected:   19,
        Strategy:           "SentenceAwareSlidingWindow");

    private static EmbedUploadingResults EmbedUpload(IReadOnlyList<string>? redFlags = null) => new(
        DocsUploaded:                  20,
        DocsFailed:                    1,
        ChunksRemoved:                 5,
        ChunksTruncated:               2,
        EmbeddingRetries:              3,
        VectorDimErrors:               0,
        VectorCacheHits:               0,
        TotalEmbeddingDurationMs:      1234,
        IndexDocumentCountSnapshot:    1000,
        IndexStorageSizeBytesSnapshot: 2_000_000,
        RedFlags:                     redFlags ?? ["upload flag"]);

    [TestMethod]
    public void AllStagesSucceeded_PopulatesEveryFieldFromTheirRespectiveResults()
    {
        var startedAt = DateTimeOffset.Parse("2024-01-01T00:00:00Z");
        var finishedAt = new DateTime(2024, 1, 1, 1, 0, 0, DateTimeKind.Utc);
        var context = MockContext("instance-1", finishedAt);

        var report = Invoke(context.Object, startedAt, new IndexRequest(ForceReindex: true),
            Extraction(), Chunking(), EmbedUpload(), success: true, error: null);

        Assert.AreEqual("instance-1", report.InstanceId);
        Assert.AreEqual(startedAt, report.StartedAt);
        Assert.AreEqual(finishedAt, report.FinishedAt);
        Assert.IsTrue(report.ForceReindex);
        Assert.IsTrue(report.Success);
        Assert.IsNull(report.ErrorMessage);
        Assert.AreEqual(1, report.DocsToProcess);
        Assert.AreEqual(20, report.ChunksProduced);
        Assert.AreEqual(20, report.DocsUploaded);
        Assert.AreEqual(1000L, report.IndexDocumentCountSnapshot);
        Assert.AreEqual(2_000_000L, report.IndexStorageSizeBytesSnapshot);
        Assert.AreEqual(1, report.Issues.Count);
        Assert.AreEqual(1, report.SpotCheckSample.Count);
    }

    [TestMethod]
    public void AllStagesNull_ProducesZeroedReportRatherThanThrowing()
    {
        var context = MockContext("instance-2", DateTime.UtcNow);

        var report = Invoke(context.Object, DateTimeOffset.UtcNow, new IndexRequest(ForceReindex: false),
            ext: null, chunk: null, embed: null, success: false, error: "boom");

        Assert.IsFalse(report.Success);
        Assert.AreEqual("boom", report.ErrorMessage);
        Assert.AreEqual(0, report.DocsToProcess);
        Assert.AreEqual(0, report.ChunksProduced);
        Assert.AreEqual(0, report.DocsUploaded);
        Assert.IsNull(report.IndexDocumentCountSnapshot);
        Assert.IsNull(report.IndexStorageSizeBytesSnapshot);
        Assert.AreEqual(0, report.Issues.Count);
        Assert.AreEqual(0, report.RedFlags.Count);
        Assert.AreEqual(0, report.SpotCheckSample.Count);
    }

    [TestMethod]
    public void OnlyExtractionSucceeded_ChunkAndEmbedFieldsAreZeroed()
    {
        var context = MockContext("instance-3", DateTime.UtcNow);

        var report = Invoke(context.Object, DateTimeOffset.UtcNow, new IndexRequest(ForceReindex: false),
            Extraction(), chunk: null, embed: null, success: false, error: "chunk activity failed");

        Assert.AreEqual(1, report.DocsToProcess);
        Assert.AreEqual(0, report.ChunksProduced);
        Assert.AreEqual(0, report.DocsUploaded);
        CollectionAssert.Contains(report.RedFlags.ToList(), "extraction flag");
    }

    [TestMethod]
    public void RedFlags_AreMergedFromExtractionAndEmbedStages()
    {
        var context = MockContext("instance-4", DateTime.UtcNow);

        var report = Invoke(context.Object, DateTimeOffset.UtcNow, new IndexRequest(ForceReindex: false),
            Extraction(redFlags: ["extract flag"]), Chunking(), EmbedUpload(redFlags: ["upload flag"]),
            success: true, error: null);

        CollectionAssert.AreEqual(new[] { "extract flag", "upload flag" }, report.RedFlags.ToList());
    }

    [TestMethod]
    public void NoRedFlagsFromEitherStage_ResultsInEmptyList()
    {
        var context = MockContext("instance-5", DateTime.UtcNow);

        var report = Invoke(context.Object, DateTimeOffset.UtcNow, new IndexRequest(ForceReindex: false),
            Extraction(redFlags: []), Chunking(), EmbedUpload(redFlags: []), success: true, error: null);

        Assert.AreEqual(0, report.RedFlags.Count);
    }

    [TestMethod]
    public void ForceReindex_IsPropagatedFromInput()
    {
        var context = MockContext("instance-6", DateTime.UtcNow);

        var report = Invoke(context.Object, DateTimeOffset.UtcNow, new IndexRequest(ForceReindex: true),
            ext: null, chunk: null, embed: null, success: true, error: null);

        Assert.IsTrue(report.ForceReindex);
    }
}
