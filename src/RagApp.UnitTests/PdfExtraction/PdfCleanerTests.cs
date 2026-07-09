using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtocolsIndexer.Models;
using ProtocolsIndexer.Services;

namespace RagApp.UnitTests.PdfExtraction;

[TestClass]
public class PdfCleanerTests
{
    private static PdfCleaner BuildCleaner() => new();

    private static PdfJoinedPageRecord Page(
        string blobName           = "doc1.pdf",
        int    pageIndex          = 0,
        string content            = "Some content",
        string title              = " Title ",
        string version            = " 7.0 ",
        string publicationDateRaw = "") => new()
    {
        BlobName           = blobName,
        PageIndex          = pageIndex,
        PageContent        = content,
        Title              = title,
        Version            = version,
        PublicationDateRaw = publicationDateRaw,
    };

    [TestMethod]
    public void ValidPage_IsCleanedAndTrimmed()
    {
        var result = BuildCleaner().Clean([Page()]);

        Assert.AreEqual(1, result.Records.Count);
        Assert.AreEqual(0, result.Errors.Count);
        var record = result.Records[0];
        Assert.AreEqual("Title", record.Title);
        Assert.AreEqual("7.0", record.Version);
        Assert.IsNull(record.PublicationDate);
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
    public void DutchLongFormDate_IsParsed()
    {
        var result = BuildCleaner().Clean([Page(publicationDateRaw: "12 maart 2024")]);

        Assert.AreEqual(new DateTime(2024, 3, 12), result.Records[0].PublicationDate);
        Assert.AreEqual(0, result.Warnings.Count(w => w.Message.Contains("could not be parsed")));
    }

    [TestMethod]
    public void ShortFormDate_IsParsed()
    {
        var result = BuildCleaner().Clean([Page(publicationDateRaw: "12-03-2024")]);

        Assert.AreEqual(new DateTime(2024, 3, 12), result.Records[0].PublicationDate);
    }

    [TestMethod]
    public void UnparseableDate_ProducesWarningAndLeavesDateNull()
    {
        var result = BuildCleaner().Clean([Page(publicationDateRaw: "not-a-date")]);

        Assert.IsNull(result.Records[0].PublicationDate);
        Assert.IsTrue(result.Warnings.Any(w => w.Message.Contains("could not be parsed")));
        Assert.AreEqual(0, result.Errors.Count);
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
