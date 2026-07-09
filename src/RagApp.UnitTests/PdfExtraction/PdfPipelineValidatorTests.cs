using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtocolsIndexer.Models;
using ProtocolsIndexer.Services;

namespace RagApp.UnitTests.PdfExtraction;

[TestClass]
public class PdfPipelineValidatorTests
{
    private static PdfJoiner            BuildJoiner()    => new();
    private static PdfCleaner           BuildCleaner()   => new();
    private static PdfPipelineValidator BuildValidator() => new();

    private static PdfJoinedPageRecord JoinedPage(string blobName, string content) => new()
    {
        BlobName    = blobName,
        PageIndex   = 0,
        PageContent = content,
    };

    // Builds a clean, all-good pipeline: one page, joined, cleaned, no errors anywhere -
    // the baseline every test below perturbs one piece of.
    private static (ExtractionResult<PdfPageRecord> Pages, ExtractionResult<PdfIndexRecord> Index, PdfJoinResult Join, PdfCleanResult Clean) HappyPath()
    {
        var pages = new ExtractionResult<PdfPageRecord>();
        pages.AddRecord(new PdfPageRecord { BlobName = "doc1.pdf", PageIndex = 0, PageContent = "## Heading\nSome markdown content" });

        var index = new ExtractionResult<PdfIndexRecord>();
        index.AddRecord(new PdfIndexRecord { BlobName = "doc1.pdf", Title = "Title", Version = "1.0" });

        var join  = BuildJoiner().Join(pages.Records, index.Records);
        var clean = BuildCleaner().Clean(join.Joined);
        return (pages, index, join, clean);
    }

    [TestMethod]
    public void HappyPath_Passes()
    {
        var (pages, index, join, clean) = HappyPath();

        var report = BuildValidator().Validate(pages, index, join, clean);

        Assert.IsTrue(report.Passed);
        Assert.AreEqual(0, report.ReconciliationProblems.Count);
    }

    [TestMethod]
    public void NoInputAtAll_FailsRatherThanPassingVacuously()
    {
        var pages = new ExtractionResult<PdfPageRecord>();
        var index = new ExtractionResult<PdfIndexRecord>();
        var join  = BuildJoiner().Join(pages.Records, index.Records);
        var clean = BuildCleaner().Clean(join.Joined);

        var report = BuildValidator().Validate(pages, index, join, clean);

        // totalAttempted == 0 forces errorRate to 100, so an empty run never passes silently.
        Assert.IsFalse(report.Passed);
    }

    [TestMethod]
    public void JoinError_IsSurfacedAsIssueAndCountsTowardErrorRate()
    {
        var pages = new ExtractionResult<PdfPageRecord>();
        pages.AddRecord(new PdfPageRecord { BlobName = "doc1.pdf", PageIndex = 0, PageContent = "Content" });
        var index = new ExtractionResult<PdfIndexRecord>(); // no matching index record for doc1
        var join  = BuildJoiner().Join(pages.Records, index.Records);
        var clean = BuildCleaner().Clean(join.Joined);

        var report = BuildValidator().Validate(pages, index, join, clean);

        Assert.IsTrue(report.Issues.Any(i => i.Stage == "Join" && i.Severity == "Error"));
    }

    [TestMethod]
    public void SkippedIndexRecord_ProducesWarningAndRedFlag()
    {
        var pages = new ExtractionResult<PdfPageRecord>();
        var index = new ExtractionResult<PdfIndexRecord>();
        index.AddRecord(new PdfIndexRecord { BlobName = "doc1.pdf", Title = "Title" });
        var join  = BuildJoiner().Join(pages.Records, index.Records);
        var clean = BuildCleaner().Clean(join.Joined);

        var report = BuildValidator().Validate(pages, index, join, clean);

        Assert.AreEqual(1, report.SkippedIndexDocuments.Count);
        Assert.IsTrue(report.RedFlags.Any(f => f.Contains("no pages")));
    }

    [TestMethod]
    public void ContentWithoutHeadings_NeedsFallbackChunking()
    {
        var joined = new List<PdfJoinedPageRecord> { JoinedPage("doc1.pdf", "Plain text, no headings at all.") };
        var clean = BuildCleaner().Clean(joined);
        var pages = new ExtractionResult<PdfPageRecord>();
        var index = new ExtractionResult<PdfIndexRecord>();
        var join  = new PdfJoinResult();

        var report = BuildValidator().Validate(pages, index, join, clean);

        CollectionAssert.Contains(report.DocumentsNeedingFallbackChunking.ToList(), "doc1.pdf");
    }

    [TestMethod]
    public void ContentWithMarkdownHeading_DoesNotNeedFallbackChunking()
    {
        var joined = new List<PdfJoinedPageRecord> { JoinedPage("doc1.pdf", "## Heading\nSome content under it.") };
        var clean = BuildCleaner().Clean(joined);
        var pages = new ExtractionResult<PdfPageRecord>();
        var index = new ExtractionResult<PdfIndexRecord>();
        var join  = new PdfJoinResult();

        var report = BuildValidator().Validate(pages, index, join, clean);

        CollectionAssert.DoesNotContain(report.DocumentsNeedingFallbackChunking.ToList(), "doc1.pdf");
    }

    [TestMethod]
    public void ReplacementCharacterInContent_IsTextQualityError()
    {
        var joined = new List<PdfJoinedPageRecord> { JoinedPage("doc1.pdf", "Corrupted � text") };
        var clean = BuildCleaner().Clean(joined);
        var pages = new ExtractionResult<PdfPageRecord>();
        var index = new ExtractionResult<PdfIndexRecord>();
        var join  = new PdfJoinResult();

        var report = BuildValidator().Validate(pages, index, join, clean);

        Assert.IsTrue(report.Issues.Any(i => i.Stage == "TextQuality" && i.Severity == "Error"));
        Assert.IsFalse(report.Passed);
    }

    [TestMethod]
    public void RepeatedTrigram_IsFlaggedAsPossibleFlattenedTable()
    {
        // Simulates a table collapsed into run-on prose: the same 3-word phrase repeats
        // across what used to be distinct rows.
        var content = string.Join(" ", Enumerable.Repeat("Naam Bond Waarde", 5));
        var joined  = new List<PdfJoinedPageRecord> { JoinedPage("doc1.pdf", content) };
        var clean   = BuildCleaner().Clean(joined);
        var pages   = new ExtractionResult<PdfPageRecord>();
        var index   = new ExtractionResult<PdfIndexRecord>();
        var join    = new PdfJoinResult();

        var report = BuildValidator().Validate(pages, index, join, clean);

        Assert.IsTrue(report.Issues.Any(i => i.Stage == "TableFlattening"));
    }

    [TestMethod]
    public void MagnitudeShiftBeyondThreshold_FailsButPassedExcludingMagnitudeIsTrue()
    {
        var (pages, index, join, clean) = HappyPath(); // 1 cleaned record

        // Previous run had 100 - a drop to 1 is a -99% shift, way past the 20% threshold.
        var report = BuildValidator().Validate(pages, index, join, clean, previousRunCleanedCount: 100);

        Assert.IsFalse(report.Passed);
        Assert.IsTrue(report.PassedExcludingMagnitude);
        Assert.AreEqual(1, report.MagnitudeWarnings.Count);
    }

    [TestMethod]
    public void MagnitudeShiftWithinThreshold_Passes()
    {
        var (pages, index, join, clean) = HappyPath(); // 1 cleaned record

        var report = BuildValidator().Validate(pages, index, join, clean, previousRunCleanedCount: 1);

        Assert.IsTrue(report.Passed);
        Assert.AreEqual(0, report.MagnitudeWarnings.Count);
    }

    [TestMethod]
    public void NoPreviousRunCount_SkipsMagnitudeCheck()
    {
        var (pages, index, join, clean) = HappyPath();

        var report = BuildValidator().Validate(pages, index, join, clean, previousRunCleanedCount: null);

        Assert.AreEqual(0, report.MagnitudeWarnings.Count);
    }

    [TestMethod]
    public void ZeroCleanedRecords_FailsEvenWithNoOtherErrors()
    {
        var pages = new ExtractionResult<PdfPageRecord>();
        var index = new ExtractionResult<PdfIndexRecord>();
        var join  = new PdfJoinResult();
        var clean = new PdfCleanResult();

        var report = BuildValidator().Validate(pages, index, join, clean);

        Assert.IsFalse(report.Passed);
        Assert.IsTrue(report.ReconciliationProblems.Any(p => p.Contains("Zero cleaned records")));
    }
}
