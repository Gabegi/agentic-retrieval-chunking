using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRag.Models;
using AgenticRag.Services;
using AgenticRag.Utils;

namespace RagApp.UnitTests.Indexing;

[TestClass]
public class ChunkingServiceTests
{
    private static Mock<IChunkingStrategy> MockStrategy(string name = "TestStrategy", Func<string, IReadOnlyList<TextChunk>>? chunkFn = null)
    {
        var mock = new Mock<IChunkingStrategy>();
        mock.SetupGet(s => s.Name).Returns(name);
        mock.Setup(s => s.Chunk(It.IsAny<string>()))
            .Returns<string>(content => chunkFn?.Invoke(content) ?? [new TextChunk(0, content)]);
        return mock;
    }

    private static ChunkingService BuildService(Mock<IChunkingStrategy> strategy) =>
        new(strategy.Object, NullLogger<ChunkingService>.Instance);

    private static ExtractionDocument Doc(
        string sourceId, int ordinal, string content, Dictionary<string, string>? metadata = null) =>
        new(sourceId, ordinal, content, metadata ?? []);

    [TestMethod]
    public void Name_PassesThroughFromStrategy()
    {
        var service = BuildService(MockStrategy(name: "MyStrategy"));

        Assert.AreEqual("MyStrategy", service.Name);
    }

    [TestMethod]
    public void Chunk_EmptyContent_ReturnsEmptyWithoutCallingStrategy()
    {
        var strategy = MockStrategy();
        var service  = BuildService(strategy);

        var result = service.Chunk("");

        Assert.AreEqual(0, result.Count);
        strategy.Verify(s => s.Chunk(It.IsAny<string>()), Times.Never);
    }

    [TestMethod]
    public void Chunk_WhitespaceContent_ReturnsEmptyWithoutCallingStrategy()
    {
        var strategy = MockStrategy();
        var service  = BuildService(strategy);

        var result = service.Chunk("   \t\n");

        Assert.AreEqual(0, result.Count);
        strategy.Verify(s => s.Chunk(It.IsAny<string>()), Times.Never);
    }

    [TestMethod]
    public void Chunk_NonEmptyContent_DelegatesToStrategy()
    {
        var strategy = MockStrategy();
        var service  = BuildService(strategy);

        var result = service.Chunk("hello world");

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("hello world", result[0].Content);
        strategy.Verify(s => s.Chunk("hello world"), Times.Once);
    }

    [TestMethod]
    public void ChunkDocuments_ComputesIdFromSourceIdOrdinalAndChunkIndex()
    {
        var service = BuildService(MockStrategy());
        var doc     = Doc("doc1", ordinal: 2, content: "content");

        var (docs, _) = service.ChunkDocuments([doc]);

        var expectedId = ChunkingUtils.SafeKey("doc1::2", 0);
        Assert.AreEqual(expectedId, docs[0].Id);
    }

    [TestMethod]
    public void ChunkDocuments_SetsDocumentIdAndPageNumberFromSourceDocument()
    {
        var service = BuildService(MockStrategy());
        var doc     = Doc("doc1", ordinal: 5, content: "content");

        var (docs, _) = service.ChunkDocuments([doc]);

        Assert.AreEqual("doc1", docs[0].DocumentId);
        Assert.AreEqual(5, docs[0].PageNumber);
        Assert.AreEqual(0, docs[0].ChunkIndex);
    }

    [TestMethod]
    public void ChunkDocuments_PrependsTitleToContent_WhenTitlePresent()
    {
        var service  = BuildService(MockStrategy());
        var metadata = new Dictionary<string, string> { ["title"] = "My Title" };
        var doc      = Doc("doc1", 0, "body text", metadata);

        var (docs, _) = service.ChunkDocuments([doc]);

        Assert.AreEqual("My Title\n\nbody text", docs[0].Content);
    }

    [TestMethod]
    public void ChunkDocuments_NoTitle_ContentIsBodyOnly()
    {
        var service = BuildService(MockStrategy());
        var doc     = Doc("doc1", 0, "body text");

        var (docs, _) = service.ChunkDocuments([doc]);

        Assert.AreEqual("body text", docs[0].Content);
    }

    [TestMethod]
    public void ChunkDocuments_PrependsHeadingBeforeTitle_WhenChunkHasHeading()
    {
        var strategy = MockStrategy(chunkFn: content => [new TextChunk(0, content, Heading: "Section 1")]);
        var service  = BuildService(strategy);
        var metadata = new Dictionary<string, string> { ["title"] = "My Title" };
        var doc      = Doc("doc1", 0, "body text", metadata);

        var (docs, _) = service.ChunkDocuments([doc]);

        Assert.AreEqual("My Title\n\nSection 1\n\nbody text", docs[0].Content);
        Assert.AreEqual("Section 1", docs[0].Heading);
    }

    [TestMethod]
    public void ChunkDocuments_MapsMetadataFieldsOntoProtocolDocument()
    {
        var service  = BuildService(MockStrategy());
        var metadata = new Dictionary<string, string>
        {
            ["title"]               = "Title",
            ["folder_path"]         = "HR/Policies",
            ["quick_code"]          = "QC-1",
            ["relative_path"]       = "a/b/c.pdf",
            ["version"]             = "2.1",
            ["last_modified_date"]  = "2024-05-01T00:00:00Z",
            ["check_date"]          = "2025-01-01T00:00:00Z",
        };
        var doc = Doc("doc1", 0, "content", metadata);

        var (docs, _) = service.ChunkDocuments([doc]);

        var result = docs[0];
        Assert.AreEqual("Title", result.Title);
        Assert.AreEqual("HR/Policies", result.Department);
        Assert.AreEqual("QC-1", result.QuickCode);
        Assert.AreEqual("a/b/c.pdf", result.RelativePath);
        Assert.AreEqual("2.1", result.Version);
        Assert.AreEqual(DateTimeOffset.Parse("2024-05-01T00:00:00Z"), result.LastModifiedDate);
        Assert.AreEqual(DateTimeOffset.Parse("2025-01-01T00:00:00Z"), result.CheckDate);
    }

    [TestMethod]
    public void ChunkDocuments_UnparsableDate_ResultsInNullDate()
    {
        var service  = BuildService(MockStrategy());
        var metadata = new Dictionary<string, string> { ["last_modified_date"] = "not-a-date" };
        var doc      = Doc("doc1", 0, "content", metadata);

        var (docs, _) = service.ChunkDocuments([doc]);

        Assert.IsNull(docs[0].LastModifiedDate);
    }

    [TestMethod]
    public void ChunkDocuments_SummaryPresent_AppliedToEveryChunkOfDocument()
    {
        var strategy = MockStrategy(chunkFn: _ => [new TextChunk(0, "part1"), new TextChunk(1, "part2")]);
        var service  = BuildService(strategy);
        var metadata = new Dictionary<string, string> { ["summary"] = "A curated summary" };
        var doc      = Doc("doc1", 0, "long content", metadata);

        var (docs, _) = service.ChunkDocuments([doc]);

        Assert.AreEqual(2, docs.Count);
        Assert.IsTrue(docs.All(d => d.Summary == "A curated summary"));
    }

    [TestMethod]
    public void ChunkDocuments_BlankSummary_MapsToNull()
    {
        var service  = BuildService(MockStrategy());
        var metadata = new Dictionary<string, string> { ["summary"] = "   " };
        var doc      = Doc("doc1", 0, "content", metadata);

        var (docs, _) = service.ChunkDocuments([doc]);

        Assert.IsNull(docs[0].Summary);
    }

    [TestMethod]
    public void ChunkDocuments_ChunkIndexIsScopedPerDocument_NotAcrossRun()
    {
        // Two docs, each producing 2 chunks — chunk index must restart at 0 for the second
        // document rather than continuing from the first (see comment in ChunkingService).
        var strategy = MockStrategy(chunkFn: content => [new TextChunk(0, content + "-a"), new TextChunk(1, content + "-b")]);
        var service  = BuildService(strategy);
        var docs     = new[] { Doc("doc1", 0, "x"), Doc("doc2", 0, "y") };

        var (result, _) = service.ChunkDocuments(docs);

        var doc2Chunks = result.Where(d => d.DocumentId == "doc2").OrderBy(d => d.ChunkIndex).ToList();
        CollectionAssert.AreEqual(new[] { 0, 1 }, doc2Chunks.Select(d => d.ChunkIndex).ToList());
    }

    [TestMethod]
    public void ChunkDocuments_OrdersBySourceIdThenOrdinal()
    {
        var strategy = MockStrategy();
        var service  = BuildService(strategy);
        var docs = new[]
        {
            Doc("docB", 1, "b1"),
            Doc("docA", 2, "a2"),
            Doc("docA", 1, "a1"),
        };

        var (result, _) = service.ChunkDocuments(docs);

        CollectionAssert.AreEqual(
            new[] { "a1", "a2", "b1" },
            result.Select(d => d.Content).ToList());
    }

    [TestMethod]
    public void ChunkDocuments_NoDocuments_ReturnsEmptyStatsAndDocs()
    {
        var service = BuildService(MockStrategy(name: "Strat"));

        var (docs, stats) = service.ChunkDocuments([]);

        Assert.AreEqual(0, docs.Count);
        Assert.AreEqual(0, stats.ChunksProduced);
        Assert.AreEqual("Strat", stats.Strategy);
    }

    [TestMethod]
    public void ChunkDocuments_StatsReflectStrategyNameAndChunkCount()
    {
        var strategy = MockStrategy(name: "Strat", chunkFn: content => [new TextChunk(0, content), new TextChunk(1, content)]);
        var service  = BuildService(strategy);

        var (docs, stats) = service.ChunkDocuments([Doc("doc1", 0, "content")]);

        Assert.AreEqual(2, docs.Count);
        Assert.AreEqual(2, stats.ChunksProduced);
        Assert.AreEqual("Strat", stats.Strategy);
    }
}
