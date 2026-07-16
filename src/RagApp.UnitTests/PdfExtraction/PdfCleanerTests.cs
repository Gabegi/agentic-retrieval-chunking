using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtocolsIndexer.Models;
using ProtocolsIndexer.Services;

namespace RagApp.UnitTests.PdfExtraction;

[TestClass]
public class PdfCleanerTests
{
    private static PdfCleaner BuildCleaner() => new();

    private static PdfPageRecord Page(
        string blobName  = "doc1.pdf",
        int    pageIndex = 0,
        string content   = "Some content",
        string title     = " Title ") => new()
    {
        BlobName    = blobName,
        PageIndex   = pageIndex,
        PageContent = content,
        Title       = title,
    };

    [TestMethod]
    public void ValidPage_IsCleanedAndTrimmed()
    {
        var result = BuildCleaner().Clean([Page()]);

        Assert.AreEqual(1, result.Records.Count);
        Assert.AreEqual(0, result.Errors.Count);
        var record = result.Records[0];
        Assert.AreEqual("Title", record.Title);
    }

    [TestMethod]
    public void DuplicatePage_SameBlobAndPageIndex_SecondIsSkipped()
    {
        var result = BuildCleaner().Clean([Page(pageIndex: 0), Page(pageIndex: 0)]);

        Assert.AreEqual(1, result.Records.Count);
        Assert.AreEqual(1, result.DuplicatePagesSkipped);
        Assert.AreEqual(1, result.Warnings.Count);
    }

    [TestMethod]
    public void SamePageIndexDifferentBlob_IsNotADuplicate()
    {
        var result = BuildCleaner().Clean([Page(blobName: "doc1.pdf", pageIndex: 0), Page(blobName: "doc2.pdf", pageIndex: 0)]);

        Assert.AreEqual(2, result.Records.Count);
        Assert.AreEqual(0, result.DuplicatePagesSkipped);
    }

    [TestMethod]
    public void EmptyContentAfterCleanup_ProducesWarningNotError()
    {
        var result = BuildCleaner().Clean([Page(content: "   ")]);

        Assert.AreEqual(1, result.Records.Count);
        Assert.AreEqual(1, result.Warnings.Count);
        Assert.AreEqual(0, result.Errors.Count);
    }

    [TestMethod]
    public void KnownMojibakePattern_IsRepairedAndCounted()
    {
        var result = BuildCleaner().Clean([Page(content: "GeÃ¯nformeerde beslissing")]);

        StringAssert.Contains(result.Records[0].PageContent, "ï");
        Assert.AreEqual(1, result.MojibakeRepairedPages);
        Assert.IsTrue(result.Warnings.Any(w => w.Message.Contains("mojibake")));
    }

    [TestMethod]
    public void ExcessBlankLines_AreCollapsed()
    {
        var result = BuildCleaner().Clean([Page(content: "Line one\n\n\n\n\nLine two")]);

        Assert.AreEqual("Line one\n\nLine two", result.Records[0].PageContent);
    }

    [TestMethod]
    public void OneBadPageAmongGoodPages_OnlyBadPageIsSkippedAsDuplicate()
    {
        var pages = new[]
        {
            Page(blobName: "doc1.pdf", pageIndex: 0),
            Page(blobName: "doc1.pdf", pageIndex: 0), // duplicate, not an error
            Page(blobName: "doc2.pdf", pageIndex: 0),
        };

        var result = BuildCleaner().Clean(pages);

        Assert.AreEqual(2, result.Records.Count);
        Assert.AreEqual(1, result.DuplicatePagesSkipped);
        Assert.AreEqual(0, result.Errors.Count);
    }
}
