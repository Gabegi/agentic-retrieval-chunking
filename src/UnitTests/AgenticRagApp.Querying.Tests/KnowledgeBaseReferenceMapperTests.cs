using System.ClientModel.Primitives;
using System.Text.Json;
using Azure.Search.Documents.KnowledgeBases.Models;
using AgenticRagApp.Querying.Services;

namespace RagApp.UnitTests.Querying;

[TestClass]
public class KnowledgeBaseReferenceMapperTests
{
    // KnowledgeBaseReference is an Azure SDK response-only model (no public constructor) -
    // built via ModelReaderWriter from JSON, the SDK's documented pattern for constructing
    // these models in tests.
    private static KnowledgeBaseReference Reference(Dictionary<string, object?>? sourceData = null)
    {
        var payload = new Dictionary<string, object?> { ["type"] = "searchIndex" };
        if (sourceData is not null)
            payload["sourceData"] = sourceData;

        var json = JsonSerializer.Serialize(payload);
        return ModelReaderWriter.Read<KnowledgeBaseReference>(BinaryData.FromString(json))!;
    }

    [TestMethod]
    public void Reference_WithNoSourceData_IsSkipped()
    {
        var reference = Reference();

        var chunks = KnowledgeBaseReferenceMapper.Map([reference]);

        Assert.AreEqual(0, chunks.Count);
    }

    [TestMethod]
    public void Reference_WithoutContentField_IsSkipped()
    {
        var reference = Reference(new() { ["id"] = "chunk1" });

        var chunks = KnowledgeBaseReferenceMapper.Map([reference]);

        Assert.AreEqual(0, chunks.Count);
    }

    [TestMethod]
    public void Reference_WithBlankContent_IsSkipped()
    {
        var reference = Reference(new() { ["content"] = "   " });

        var chunks = KnowledgeBaseReferenceMapper.Map([reference]);

        Assert.AreEqual(0, chunks.Count);
    }

    [TestMethod]
    public void Reference_WithAllFields_MapsToRetrievedChunk()
    {
        var reference = Reference(new()
        {
            ["id"]            = "chunk1",
            ["document_id"]   = "doc1",
            ["title"]         = "Title",
            ["summary"]       = "Summary",
            ["page_number"]   = 3,
            ["chunk_index"]   = 1,
            ["quick_code"]    = "QC-1",
            ["relative_path"] = "a/b.pdf",
            ["content"]       = "The chunk content",
        });

        var chunks = KnowledgeBaseReferenceMapper.Map([reference]);

        Assert.AreEqual(1, chunks.Count);
        var chunk = chunks[0];
        Assert.AreEqual("chunk1", chunk.Id);
        Assert.AreEqual("doc1", chunk.DocumentId);
        Assert.AreEqual("Title", chunk.Title);
        Assert.AreEqual("Summary", chunk.Summary);
        Assert.AreEqual(3, chunk.Page);
        Assert.AreEqual(1, chunk.ChunkIndex);
        Assert.AreEqual("QC-1", chunk.QuickCode);
        Assert.AreEqual("a/b.pdf", chunk.RelativePath);
        Assert.AreEqual("The chunk content", chunk.Content);
    }

    [TestMethod]
    public void Reference_MissingOptionalIntFields_DefaultToZero()
    {
        var reference = Reference(new() { ["content"] = "text only" });

        var chunks = KnowledgeBaseReferenceMapper.Map([reference]);

        Assert.AreEqual(1, chunks.Count);
        Assert.AreEqual(0, chunks[0].Page);
        Assert.AreEqual(0, chunks[0].ChunkIndex);
        Assert.AreEqual("", chunks[0].Id);
        Assert.AreEqual("", chunks[0].DocumentId);
        Assert.IsNull(chunks[0].Title);
    }

    [TestMethod]
    public void MultipleReferences_AllValidOnesAreMapped_InOrder()
    {
        var ref1 = Reference(new() { ["id"] = "a", ["content"] = "content A" });
        var ref2 = Reference(new() { ["id"] = "b" }); // no content -> skipped
        var ref3 = Reference(new() { ["id"] = "c", ["content"] = "content C" });

        var chunks = KnowledgeBaseReferenceMapper.Map([ref1, ref2, ref3]);

        CollectionAssert.AreEqual(new[] { "a", "c" }, chunks.Select(c => c.Id).ToList());
    }
}
