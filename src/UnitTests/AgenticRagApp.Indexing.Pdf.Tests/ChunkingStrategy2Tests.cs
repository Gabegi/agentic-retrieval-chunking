using Microsoft.VisualStudio.TestTools.UnitTesting;
using AgenticRagApp.Indexing.Pdf.Services;

namespace RagApp.UnitTests;

[TestClass]
public class ChunkingStrategy2Tests
{
    [TestMethod]
    public void SmallTable_FitsInOneChunk_IsNeverSplit()
    {
        var table = "| Name | Dose |\n|---|---|\n| Aspirin | 100mg |\n| Ibuprofen | 200mg |";
        var strategy = new ChunkingStrategy2(maxChars: 1500);

        var chunks = strategy.Chunk(table);

        Assert.AreEqual(1, chunks.Count);
        Assert.AreEqual(table, chunks[0].Content);
    }

    [TestMethod]
    public void OversizedTable_SplitsRowsAndRepeatsHeaderOnEveryChunk()
    {
        var table =
            "| Name | Dose |\n" +
            "|---|---|\n" +
            "| Aspirin | 100mg |\n" +
            "| Ibuprofen | 200mg |\n" +
            "| Paracetamol | 500mg |\n" +
            "| Amoxicillin | 250mg |";
        var strategy = new ChunkingStrategy2(maxChars: 45); // forces multiple row-groups

        var chunks = strategy.Chunk(table);

        Assert.IsTrue(chunks.Count > 1, "expected the table to split into more than one chunk");
        foreach (var chunk in chunks)
        {
            StringAssert.StartsWith(chunk.Content, "| Name | Dose |");
            StringAssert.Contains(chunk.Content, "|---|---|");
        }
        // Every data row must survive somewhere across the split chunks.
        var combined = string.Join("\n", chunks.Select(c => c.Content));
        StringAssert.Contains(combined, "Aspirin");
        StringAssert.Contains(combined, "Ibuprofen");
        StringAssert.Contains(combined, "Paracetamol");
        StringAssert.Contains(combined, "Amoxicillin");
    }

    [TestMethod]
    public void ProseOnlyContent_ChunksTheSameAsUnderlyingStrategy1()
    {
        var text = "This is sentence one. This is sentence two. This is sentence three.";
        var strategy = new ChunkingStrategy2(maxChars: 1500);

        var chunks = strategy.Chunk(text);

        Assert.AreEqual(1, chunks.Count);
        Assert.AreEqual(text, chunks[0].Content);
    }

    [TestMethod]
    public void ProseContainingALonePipeCharacter_IsNotMisclassifiedAsTable()
    {
        var text = "The cost is |20 depending on the flavor selected today.";
        var strategy = new ChunkingStrategy2(maxChars: 1500);

        var chunks = strategy.Chunk(text);

        Assert.AreEqual(1, chunks.Count);
        Assert.AreEqual(text, chunks[0].Content);
    }

    [TestMethod]
    public void TableSurroundedByProse_TableStaysIntactAsItsOwnChunk()
    {
        var content =
            "Some introductory prose about dosing.\n\n" +
            "| Name | Dose |\n|---|---|\n| Aspirin | 100mg |\n\n" +
            "Some concluding remarks about the table above.";
        var strategy = new ChunkingStrategy2(maxChars: 1500);

        var chunks = strategy.Chunk(content);

        Assert.IsTrue(chunks.Any(c => c.Content.Contains("| Name | Dose |") && c.Content.Contains("Aspirin")));
    }
}
