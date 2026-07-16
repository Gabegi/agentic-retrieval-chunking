using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtocolsIndexer.Models;
using ProtocolsIndexer.Services;

namespace RagApp.UnitTests.PdfExtraction;

[TestClass]
public class PdfPipelineValidatorTests
{
    private static PdfCleaner           BuildCleaner()   => new();
    private static PdfPipelineValidator BuildValidator() => new();

    private static PdfPageRecord Page(string blobName, string content, string title = "Title") => new()
    {
        BlobName    = blobName,
        PageIndex   = 0,
        PageContent = content,
        Title       = title,
    };

    // Builds a clean, all-good pipeline: one page, cleaned, no errors anywhere - the
    // baseline every test below perturbs one piece of.
    private static (ExtractionResult<PdfPageRecord> Pages, PdfCleanResult Clean) HappyPath()
    {
        var fileExtraction = new PdfFileExtraction(
            Pages: [new PdfPageRecord { BlobName = "doc1.pdf", PageIndex = 0, PageContent = "## Heading\nSome markdown content", Title = "Title" }],
            Error: null);
        var pages = PdfExtractionAggregation.Aggregate([fileExtraction]);
        var clean = BuildCleaner().Clean(pages.Records);
        return (pages, clean);
    }

    [TestMethod]
    public void HappyPath_Passes()
    {
        var (pages, clean) = HappyPath();

        var report = BuildValidator().Validate(pages, clean);

        Assert.IsTrue(report.Passed);
        Assert.AreEqual(0, report.ReconciliationProblems.Count);
    }

    [TestMethod]
    public void NoInputAtAll_FailsRatherThanPassingVacuously()
    {
        var pages = new ExtractionResult<PdfPageRecord>();
        var clean = BuildCleaner().Clean(pages.Records);

        var report = BuildValidator().Validate(pages, clean);

        // totalAttempted == 0 forces errorRate to 100, so an empty run never passes silently.
        Assert.IsFalse(report.Passed);
    }

    [TestMethod]
    public void ContentWithoutHeadings_NeedsFallbackChunking()
    {
        var clean = BuildCleaner().Clean([Page("doc1.pdf", "Plain text, no headings at all.")]);
        var pages = new ExtractionResult<PdfPageRecord>();

        var report = BuildValidator().Validate(pages, clean);

        CollectionAssert.Contains(report.DocumentsNeedingFallbackChunking.ToList(), "doc1.pdf");
    }

    [TestMethod]
    public void ContentWithMarkdownHeading_DoesNotNeedFallbackChunking()
    {
        var clean = BuildCleaner().Clean([Page("doc1.pdf", "## Heading\nSome content under it.")]);
        var pages = new ExtractionResult<PdfPageRecord>();

        var report = BuildValidator().Validate(pages, clean);

        CollectionAssert.DoesNotContain(report.DocumentsNeedingFallbackChunking.ToList(), "doc1.pdf");
    }

    [TestMethod]
    public void ReplacementCharacterInContent_IsTextQualityError()
    {
        var clean = BuildCleaner().Clean([Page("doc1.pdf", "Corrupted � text")]);
        var pages = new ExtractionResult<PdfPageRecord>();

        var report = BuildValidator().Validate(pages, clean);

        Assert.IsTrue(report.Issues.Any(i => i.Stage == "TextQuality" && i.Severity == "Error"));
        Assert.IsFalse(report.Passed);
    }

    [TestMethod]
    public void RepeatedTrigram_IsFlaggedAsPossibleFlattenedTable()
    {
        // Simulates a table collapsed into run-on prose: the same 3-word phrase repeats
        // across what used to be distinct rows.
        var content = string.Join(" ", Enumerable.Repeat("Naam Bond Waarde", 5));
        var clean   = BuildCleaner().Clean([Page("doc1.pdf", content)]);
        var pages   = new ExtractionResult<PdfPageRecord>();

        var report = BuildValidator().Validate(pages, clean);

        Assert.IsTrue(report.Issues.Any(i => i.Stage == "TableFlattening"));
    }

    [TestMethod]
    public void MagnitudeShiftBeyondThreshold_FailsButPassedExcludingMagnitudeIsTrue()
    {
        var (pages, clean) = HappyPath(); // 1 cleaned record

        // Previous run had 100 - a drop to 1 is a -99% shift, way past the 20% threshold.
        var report = BuildValidator().Validate(pages, clean, previousRunCleanedCount: 100);

        Assert.IsFalse(report.Passed);
        Assert.IsTrue(report.PassedExcludingMagnitude);
        Assert.AreEqual(1, report.MagnitudeWarnings.Count);
    }

    [TestMethod]
    public void MagnitudeShiftWithinThreshold_Passes()
    {
        var (pages, clean) = HappyPath(); // 1 cleaned record

        var report = BuildValidator().Validate(pages, clean, previousRunCleanedCount: 1);

        Assert.IsTrue(report.Passed);
        Assert.AreEqual(0, report.MagnitudeWarnings.Count);
    }

    [TestMethod]
    public void NoPreviousRunCount_SkipsMagnitudeCheck()
    {
        var (pages, clean) = HappyPath();

        var report = BuildValidator().Validate(pages, clean, previousRunCleanedCount: null);

        Assert.AreEqual(0, report.MagnitudeWarnings.Count);
    }

    [TestMethod]
    public void ZeroCleanedRecords_FailsEvenWithNoOtherErrors()
    {
        var pages = new ExtractionResult<PdfPageRecord>();
        var clean = new PdfCleanResult();

        var report = BuildValidator().Validate(pages, clean);

        Assert.IsFalse(report.Passed);
        Assert.IsTrue(report.ReconciliationProblems.Any(p => p.Contains("Zero cleaned records")));
    }
}
