using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtocolsIndexer.Models;
using ProtocolsIndexer.Services;

namespace RagApp.UnitTests.CsvExtraction;

[TestClass]
public class PipelineValidatorTests
{
    // Builds a clean, all-good pipeline: one page, joined, cleaned, no errors anywhere -
    // the baseline every test below perturbs one piece of.
    private static (ExtractionResult<PageRecord> Pages, ExtractionResult<IndexRecord> Index, JoinResult Join, CleanResult Clean) HappyPath()
    {
        var pages = CsvExtractor.ExtractPages(ToStream(
            "DOCUMENT_ID,TITLE,QUICK_CODE,FOLDER_MINI_FULL_PATH,LAST_MODIFIED_DATETIME,PAGE_INDEX,PAGE_CONTENT,RELATIVE_PATH\n" +
            "doc1,Title,QC,Folder,20240101120000,0,Some markdown content,rel\n"));
        var index = CsvExtractor.ExtractIndex(ToStream(
            "DOCUMENT_ID,DOCUMENT_TYPE_NAME,SUMMARY,VERSION,CHECK_DATE,ATTENTION_REQUIRED_FLAGS\n" +
            "doc1,Protocol,Summary,7.0,,[]\n"));
        var join  = CsvJoiner.Join(pages.Records, index.Records);
        var clean = DataCleaner.Clean(join.Joined);
        return (pages, index, join, clean);
    }

    private static Stream ToStream(string csv) => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv));

    [TestMethod]
    public void HappyPath_Passes()
    {
        var (pages, index, join, clean) = HappyPath();

        var report = PipelineValidator.Validate(pages, index, join, clean);

        Assert.IsTrue(report.Passed);
        Assert.AreEqual(0, report.ReconciliationProblems.Count);
    }

    [TestMethod]
    public void NoInputAtAll_FailsRatherThanPassingVacuously()
    {
        var pages = new ExtractionResult<PageRecord>();
        var index = new ExtractionResult<IndexRecord>();
        var join  = CsvJoiner.Join(pages.Records, index.Records);
        var clean = DataCleaner.Clean(join.Joined);

        var report = PipelineValidator.Validate(pages, index, join, clean);

        // totalAttempted == 0 forces errorRate to 100, so an empty run never passes silently.
        Assert.IsFalse(report.Passed);
    }

    [TestMethod]
    public void JoinError_IsSurfacedAsIssueAndCountsTowardErrorRate()
    {
        var pages = CsvExtractor.ExtractPages(ToStream(
            "DOCUMENT_ID,TITLE,QUICK_CODE,FOLDER_MINI_FULL_PATH,LAST_MODIFIED_DATETIME,PAGE_INDEX,PAGE_CONTENT,RELATIVE_PATH\n" +
            "doc1,Title,QC,Folder,20240101120000,0,Content,rel\n"));
        var index = new ExtractionResult<IndexRecord>(); // no matching index record for doc1
        var join  = CsvJoiner.Join(pages.Records, index.Records);
        var clean = DataCleaner.Clean(join.Joined);

        var report = PipelineValidator.Validate(pages, index, join, clean);

        Assert.IsTrue(report.Issues.Any(i => i.Stage == "Join" && i.Severity == "Error"));
    }

    [TestMethod]
    public void SkippedIndexRecord_ProducesWarningAndRedFlag()
    {
        var pages = new ExtractionResult<PageRecord>();
        var index = CsvExtractor.ExtractIndex(ToStream(
            "DOCUMENT_ID,DOCUMENT_TYPE_NAME,SUMMARY,VERSION,CHECK_DATE,ATTENTION_REQUIRED_FLAGS\n" +
            "doc1,Protocol,Summary,7.0,,[]\n"));
        var join  = CsvJoiner.Join(pages.Records, index.Records);
        var clean = DataCleaner.Clean(join.Joined);

        var report = PipelineValidator.Validate(pages, index, join, clean);

        Assert.AreEqual(1, report.SkippedIndexDocuments.Count);
        Assert.IsTrue(report.RedFlags.Any(f => f.Contains("no pages")));
    }

    [TestMethod]
    public void StaleDocument_IsFlaggedAndCounted()
    {
        var flaggedJoin = new List<JoinedPageRecord>
        {
            new()
            {
                DocumentId        = "doc1",
                PageIndex         = 0,
                PageContent       = "content",
                AttentionFlagsRaw = "[\"check_date_exceeded\"]",
            },
        };
        var clean = DataCleaner.Clean(flaggedJoin);

        var pages = new ExtractionResult<PageRecord>();
        var index = new ExtractionResult<IndexRecord>();
        var join  = new JoinResult();

        var report = PipelineValidator.Validate(pages, index, join, clean);

        Assert.AreEqual(1, report.StaleDocCount);
        Assert.IsTrue(report.RedFlags.Any(f => f.Contains("check_date_exceeded")));
    }

    [TestMethod]
    public void ContentWithoutHeadings_NeedsFallbackChunking()
    {
        var joined = new List<JoinedPageRecord>
        {
            new() { DocumentId = "doc1", PageIndex = 0, PageContent = "Plain text, no headings at all." },
        };
        var clean = DataCleaner.Clean(joined);
        var pages = new ExtractionResult<PageRecord>();
        var index = new ExtractionResult<IndexRecord>();
        var join  = new JoinResult();

        var report = PipelineValidator.Validate(pages, index, join, clean);

        CollectionAssert.Contains(report.DocumentsNeedingFallbackChunking.ToList(), "doc1");
    }

    [TestMethod]
    public void ContentWithMarkdownHeading_DoesNotNeedFallbackChunking()
    {
        var joined = new List<JoinedPageRecord>
        {
            new() { DocumentId = "doc1", PageIndex = 0, PageContent = "# Heading\nSome content under it." },
        };
        var clean = DataCleaner.Clean(joined);
        var pages = new ExtractionResult<PageRecord>();
        var index = new ExtractionResult<IndexRecord>();
        var join  = new JoinResult();

        var report = PipelineValidator.Validate(pages, index, join, clean);

        CollectionAssert.DoesNotContain(report.DocumentsNeedingFallbackChunking.ToList(), "doc1");
    }

    [TestMethod]
    public void ReplacementCharacterInContent_IsTextQualityError()
    {
        var joined = new List<JoinedPageRecord>
        {
            new() { DocumentId = "doc1", PageIndex = 0, PageContent = "Corrupted � text" },
        };
        var clean = DataCleaner.Clean(joined);
        var pages = new ExtractionResult<PageRecord>();
        var index = new ExtractionResult<IndexRecord>();
        var join  = new JoinResult();

        var report = PipelineValidator.Validate(pages, index, join, clean);

        Assert.IsTrue(report.Issues.Any(i => i.Stage == "TextQuality" && i.Severity == "Error"));
        Assert.IsFalse(report.Passed);
    }

    [TestMethod]
    public void NonDutchLanguage_IsTextQualityWarning()
    {
        var joined = new List<JoinedPageRecord>
        {
            new() { DocumentId = "doc1", PageIndex = 0, PageContent = "Some content", Language = "en-US" },
        };
        var clean = DataCleaner.Clean(joined);
        var pages = new ExtractionResult<PageRecord>();
        var index = new ExtractionResult<IndexRecord>();
        var join  = new JoinResult();

        var report = PipelineValidator.Validate(pages, index, join, clean);

        Assert.IsTrue(report.Issues.Any(i => i.Stage == "TextQuality" && i.Severity == "Warning" && i.Message.Contains("en-US")));
    }

    [TestMethod]
    public void MagnitudeShiftBeyondThreshold_FailsButPassedExcludingMagnitudeIsTrue()
    {
        var (pages, index, join, clean) = HappyPath(); // 1 cleaned record

        // Previous run had 100 - a drop to 1 is a -99% shift, way past the 20% threshold.
        var report = PipelineValidator.Validate(pages, index, join, clean, previousRunCleanedCount: 100);

        Assert.IsFalse(report.Passed);
        Assert.IsTrue(report.PassedExcludingMagnitude);
        Assert.AreEqual(1, report.MagnitudeWarnings.Count);
    }

    [TestMethod]
    public void MagnitudeShiftWithinThreshold_Passes()
    {
        var (pages, index, join, clean) = HappyPath(); // 1 cleaned record

        var report = PipelineValidator.Validate(pages, index, join, clean, previousRunCleanedCount: 1);

        Assert.IsTrue(report.Passed);
        Assert.AreEqual(0, report.MagnitudeWarnings.Count);
    }

    [TestMethod]
    public void NoPreviousRunCount_SkipsMagnitudeCheck()
    {
        var (pages, index, join, clean) = HappyPath();

        var report = PipelineValidator.Validate(pages, index, join, clean, previousRunCleanedCount: null);

        Assert.AreEqual(0, report.MagnitudeWarnings.Count);
    }
}
