using AgenticRagApp.Observability.Reports;

namespace RagApp.UnitTests.Functions;

// PdfIndexRunReport.FromResults is the single biggest risk hotspot in the assembly (highest
// cyclomatic complexity, all null-coalescing over the three stage results). It's pure data
// assembly with no I/O, so it's exercised directly rather than through the orchestrator.
[TestClass]
public class PdfIndexRunReportFromResultsTests
{
    private static PdfIndexRunReport Invoke(
        string instanceId, DateTimeOffset startedAt, DateTimeOffset finishedAt, bool forceReindex,
        ExtractionResults? ext, ChunkingResults? chunk, EmbedUploadingResults? embed,
        bool success, string? error) =>
        PdfIndexRunReport.FromResults(instanceId, startedAt, finishedAt, forceReindex, ext, chunk, embed, success, error);

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
        TraceabilityGapCount:   11,
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
        var finishedAt = DateTimeOffset.Parse("2024-01-01T01:00:00Z");

        var report = Invoke("instance-1", startedAt, finishedAt, forceReindex: true,
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
        Assert.AreEqual(11, report.TraceabilityGapCount);
    }

    [TestMethod]
    public void AllStagesNull_ProducesZeroedReportRatherThanThrowing()
    {
        var report = Invoke("instance-2", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, forceReindex: false,
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
        Assert.AreEqual(0, report.TraceabilityGapCount);
    }

    [TestMethod]
    public void OnlyExtractionSucceeded_ChunkAndEmbedFieldsAreZeroed()
    {
        var report = Invoke("instance-3", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, forceReindex: false,
            Extraction(), chunk: null, embed: null, success: false, error: "chunk activity failed");

        Assert.AreEqual(1, report.DocsToProcess);
        Assert.AreEqual(0, report.ChunksProduced);
        Assert.AreEqual(0, report.DocsUploaded);
        CollectionAssert.Contains(report.RedFlags.ToList(), "extraction flag");
    }

    [TestMethod]
    public void RedFlags_AreMergedFromExtractionAndEmbedStages()
    {
        var report = Invoke("instance-4", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, forceReindex: false,
            Extraction(redFlags: ["extract flag"]), Chunking(), EmbedUpload(redFlags: ["upload flag"]),
            success: true, error: null);

        CollectionAssert.AreEqual(new[] { "extract flag", "upload flag" }, report.RedFlags.ToList());
    }

    [TestMethod]
    public void NoRedFlagsFromEitherStage_ResultsInEmptyList()
    {
        var report = Invoke("instance-5", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, forceReindex: false,
            Extraction(redFlags: []), Chunking(), EmbedUpload(redFlags: []), success: true, error: null);

        Assert.AreEqual(0, report.RedFlags.Count);
    }

    [TestMethod]
    public void ForceReindex_IsPropagatedFromInput()
    {
        var report = Invoke("instance-6", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, forceReindex: true,
            ext: null, chunk: null, embed: null, success: true, error: null);

        Assert.IsTrue(report.ForceReindex);
    }
}
