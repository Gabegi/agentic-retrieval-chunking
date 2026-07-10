using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtocolsIndexer.Services;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace RagApp.UnitTests.PdfExtraction;

[TestClass]
public class PdfPigExtractorTests
{
    // Builds a minimal one-page PDF: a large bold heading line followed by smaller
    // regular body text, so PdfPigExtractor's font-size/bold heuristics have a real
    // heading to detect - no real Cordaan sample PDF needed for this level of coverage.
    private static byte[] BuildSamplePdf(string heading, string body)
    {
        var builder      = new PdfDocumentBuilder();
        var page         = builder.AddPage(PageSize.A4);
        var boldFont     = builder.AddStandard14Font(Standard14Font.HelveticaBold);
        var regularFont  = builder.AddStandard14Font(Standard14Font.Helvetica);

        page.AddText(heading, 24, new PdfPoint(50, 700), boldFont);
        page.AddText(body, 10, new PdfPoint(50, 650), regularFont);

        return builder.Build();
    }

    [TestMethod]
    public void LargeBoldLine_IsDetectedAsHeading()
    {
        var bytes  = BuildSamplePdf("Introductie", "This is regular body text about the topic.");
        var result = new PdfPigExtractor().ExtractPDF("doc1.pdf", bytes);

        Assert.IsNull(result.Error);
        Assert.AreEqual(1, result.Pages.Count);
        StringAssert.Contains(result.Pages[0].PageContent, "## Introductie");
        StringAssert.Contains(result.Pages[0].PageContent, "regular body text");
    }

    [TestMethod]
    public void PageIndex_MatchesPdfPageNumber()
    {
        var bytes  = BuildSamplePdf("Heading", "Body text.");
        var result = new PdfPigExtractor().ExtractPDF("doc1.pdf", bytes);

        Assert.AreEqual(1, result.Pages[0].PageIndex);
    }

    [TestMethod]
    public void BlobName_IsCarriedOntoEveryPage()
    {
        var bytes  = BuildSamplePdf("Heading", "Body text.");
        var result = new PdfPigExtractor().ExtractPDF("some/blob/doc1.pdf", bytes);

        Assert.IsTrue(result.Pages.All(p => p.BlobName == "some/blob/doc1.pdf"));
    }

    [TestMethod]
    public void KnownSectionVocabulary_IsDetectedAsHeadingRegardlessOfFontSize()
    {
        // There's no default known-section vocabulary (no confirmed template to build
        // one from), but a caller-supplied vocabulary should still be matched exactly,
        // even at body font size.
        var builder     = new PdfDocumentBuilder();
        var page        = builder.AddPage(PageSize.A4);
        var regularFont = builder.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText("Samenvatting", 10, new PdfPoint(50, 700), regularFont);
        page.AddText("Body text follows.", 10, new PdfPoint(50, 650), regularFont);
        var bytes = builder.Build();

        var result = new PdfPigExtractor(knownSections: ["Samenvatting"]).ExtractPDF("doc1.pdf", bytes);

        StringAssert.Contains(result.Pages[0].PageContent, "## Samenvatting");
    }

    [TestMethod]
    public void CorruptBytes_ProducesFileLevelErrorNotException()
    {
        var result = new PdfPigExtractor().ExtractPDF("corrupt.pdf", "not a real pdf"u8.ToArray());

        Assert.IsNotNull(result.Error);
        Assert.AreEqual(0, result.Pages.Count);
        Assert.IsNull(result.Index);
    }
}
