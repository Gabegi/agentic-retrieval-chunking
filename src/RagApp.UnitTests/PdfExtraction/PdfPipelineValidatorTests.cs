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

    private static PdfDocumentStructure Structure(params TableInfo[] tables) => new(
        Headings: [],
        Boilerplate: [],
        Tables: tables,
        PageDimensions: [],
        SelectionMarks: [],
        Figures: [],
        HandwrittenSpans: [],
        Lines: []);

    // Builds a clean, all-good pipeline: one page, cleaned, no errors anywhere - the
    // baseline every test below perturbs one piece of.
    private static (ExtractionResult<PdfPageRecord> Pages, PdfCleanResult Clean) HappyPath()
    {
        var fileExtraction = new PDFExtractionResult(
            Ok:               true,
            BlobName:         "doc1.pdf",
            FileSizeBytes:    1024,
            PdfSpecVersion:   1.7,
            NativeMetadata:   null,
            RawContent:       "## Heading\nSome markdown content",
            Pages:            [new PdfPageRecord { BlobName = "doc1.pdf", PageIndex = 0, PageContent = "## Heading\nSome markdown content", Title = "Title" }],
            Structure:        null,
            EstimatedCostUsd: 0.01m,
            Error:            null);
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

    [TestMethod]
    public void DetectedTableCount_SumsRealTableDataAcrossFiles()
    {
        var clean = BuildCleaner().Clean([Page("doc1.pdf", "Some content")]);
        var pages = new ExtractionResult<PdfPageRecord>();
        var structures = new Dictionary<string, PdfDocumentStructure>
        {
            ["doc1.pdf"] = Structure(
                new TableInfo(2, 3, [new TableCellInfo(0, 0, "content", "a")], Offset: 0, PageNumber: 1),
                new TableInfo(1, 1, [new TableCellInfo(0, 0, "content", "b")], Offset: 10, PageNumber: 1)),
        };

        var report = BuildValidator().Validate(pages, clean, structures: structures);

        Assert.AreEqual(2, report.DetectedTableCount);
    }

    [TestMethod]
    public void MalformedTable_ZeroRowsOrColumns_IsFlaggedAsWarning()
    {
        var clean = BuildCleaner().Clean([Page("doc1.pdf", "Some content")]);
        var pages = new ExtractionResult<PdfPageRecord>();
        var structures = new Dictionary<string, PdfDocumentStructure>
        {
            ["doc1.pdf"] = Structure(new TableInfo(0, 0, [], Offset: 0, PageNumber: 1)),
        };

        var report = BuildValidator().Validate(pages, clean, structures: structures);

        Assert.IsTrue(report.Issues.Any(i =>
            i.Stage == "TextQuality" && i.Severity == "Warning" && i.Message.Contains("malformed")));
    }

    [TestMethod]
    public void TableWithNoCellData_IsFlaggedAsWarning()
    {
        var clean = BuildCleaner().Clean([Page("doc1.pdf", "Some content")]);
        var pages = new ExtractionResult<PdfPageRecord>();
        var structures = new Dictionary<string, PdfDocumentStructure>
        {
            ["doc1.pdf"] = Structure(new TableInfo(2, 2, [], Offset: 0, PageNumber: 1)),
        };

        var report = BuildValidator().Validate(pages, clean, structures: structures);

        Assert.IsTrue(report.Issues.Any(i =>
            i.Stage == "TextQuality" && i.Severity == "Warning" && i.Message.Contains("no cell data")));
    }

    [TestMethod]
    public void NoStructuresProvided_DetectedTableCountIsZeroAndNoQualityIssues()
    {
        var clean = BuildCleaner().Clean([Page("doc1.pdf", "Some content")]);
        var pages = new ExtractionResult<PdfPageRecord>();

        var report = BuildValidator().Validate(pages, clean);

        Assert.AreEqual(0, report.DetectedTableCount);
        Assert.IsFalse(report.Issues.Any(i => i.Stage == "TextQuality"));
    }
}
