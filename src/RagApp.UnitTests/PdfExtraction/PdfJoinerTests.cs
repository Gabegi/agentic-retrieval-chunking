using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtocolsIndexer.Models;
using ProtocolsIndexer.Services;

namespace RagApp.UnitTests.PdfExtraction;

[TestClass]
public class PdfJoinerTests
{
    private static PdfJoiner BuildJoiner() => new();

    private static PdfPageRecord Page(string blobName, int pageIndex = 0, string content = "content") => new()
    {
        BlobName    = blobName,
        PageIndex   = pageIndex,
        PageContent = content,
    };

    private static PdfIndexRecord Index(string blobName, string title = "Title") => new()
    {
        BlobName = blobName,
        Title    = title,
    };

    [TestMethod]
    public void Matched_IsAddedToJoined()
    {
        var result = BuildJoiner().Join([Page("doc1.pdf")], [Index("doc1.pdf")]);

        Assert.AreEqual(1, result.Joined.Count);
        Assert.AreEqual("doc1.pdf", result.Joined[0].BlobName);
        Assert.AreEqual(0, result.Errors.Count);
        Assert.AreEqual(0, result.DataQualityWarnings.Count);
    }

    [TestMethod]
    public void PageWithNoMatchingIndex_IsError()
    {
        var result = BuildJoiner().Join([Page("doc1.pdf")], []);

        Assert.AreEqual(0, result.Joined.Count);
        Assert.AreEqual(1, result.Errors.Count);
        Assert.AreEqual("doc1.pdf", result.Errors[0].DocumentId);
    }

    [TestMethod]
    public void MultiplePagesForSameUnmatchedDoc_OnlyReportsErrorOnce()
    {
        var result = BuildJoiner().Join([Page("doc1.pdf", 0), Page("doc1.pdf", 1)], []);

        Assert.AreEqual(1, result.Errors.Count);
    }

    [TestMethod]
    public void IndexRecordWithNoMatchingPages_IsSkipped()
    {
        var result = BuildJoiner().Join([], [Index("doc1.pdf")]);

        Assert.AreEqual(1, result.SkippedIndexRecords.Count);
        Assert.AreEqual("doc1.pdf", result.SkippedIndexRecords[0].BlobName);
    }

    [TestMethod]
    public void DuplicateIndexBlobName_KeepsFirstOccurrenceAndWarnsOnce()
    {
        var result = BuildJoiner().Join(
            [Page("doc1.pdf")],
            [Index("doc1.pdf", title: "First"), Index("doc1.pdf", title: "Second")]);

        Assert.AreEqual(1, result.Joined.Count);
        Assert.AreEqual("First", result.Joined[0].Title);
        Assert.AreEqual(1, result.DataQualityWarnings.Count);
    }

    [TestMethod]
    public void BlobNameMatching_IsCaseInsensitive()
    {
        var result = BuildJoiner().Join([Page("DOC1.PDF")], [Index("doc1.pdf")]);

        Assert.AreEqual(1, result.Joined.Count);
    }

    [TestMethod]
    public void AllPagesForDocument_AreJoinedToSameIndexRecord()
    {
        var result = BuildJoiner().Join([Page("doc1.pdf", 0), Page("doc1.pdf", 1), Page("doc1.pdf", 2)], [Index("doc1.pdf")]);

        Assert.AreEqual(3, result.Joined.Count);
        Assert.IsTrue(result.Joined.All(r => r.Title == "Title"));
    }

    [TestMethod]
    public void MixedBatch_ClassifiesEachDocumentIndependently()
    {
        var pages = new[]
        {
            Page("matched.pdf"),
            Page("notfound.pdf"),
        };
        var index = new[]
        {
            Index("matched.pdf"),
            Index("orphan.pdf"), // no pages -> skipped
        };

        var result = BuildJoiner().Join(pages, index);

        Assert.AreEqual(1, result.Joined.Count);
        Assert.AreEqual("matched.pdf", result.Joined[0].BlobName);
        Assert.AreEqual(1, result.Errors.Count);
        Assert.AreEqual("notfound.pdf", result.Errors[0].DocumentId);
        Assert.AreEqual(1, result.SkippedIndexRecords.Count);
        Assert.AreEqual("orphan.pdf", result.SkippedIndexRecords[0].BlobName);
    }
}
