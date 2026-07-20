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
        var result = BuildCleaner().CleanPdf([Page()]);

        Assert.AreEqual(1, result.Records.Count);
        Assert.AreEqual(0, result.Errors.Count);
        var record = result.Records[0];
        Assert.AreEqual("Title", record.Title);
    }

    [TestMethod]
    public void EmptyContentAfterCleanup_ProducesWarningNotError()
    {
        var result = BuildCleaner().CleanPdf([Page(content: "   ")]);

        Assert.AreEqual(1, result.Records.Count);
        Assert.AreEqual(1, result.Warnings.Count);
        Assert.AreEqual(0, result.Errors.Count);
    }

    [TestMethod]
    public void Mojibake_IsRepairedAndCounted()
    {
        var result = BuildCleaner().CleanPdf([Page(content: "GeÃ¯nformeerde beslissing")]);

        StringAssert.Contains(result.Records[0].PageContent, "ï");
        Assert.AreEqual(1, result.MojibakeRepairedPages);
        Assert.IsTrue(result.Warnings.Any(w => w.Message.Contains("mojibake")));
    }

    [TestMethod]
    public void ExcessBlankLines_AreCollapsed()
    {
        var result = BuildCleaner().CleanPdf([Page(content: "Line one\n\n\n\n\nLine two")]);

        Assert.AreEqual("Line one\n\nLine two", result.Records[0].PageContent);
    }

    // PdfCleaner no longer matches a fixed pattern table - it round-trips the whole page
    // through Windows-1252 -> UTF-8 whenever 'Ã'/'â' appear, repairing the entire mis-decode
    // class in one pass. Coverage is round-trip cases, not a pattern-ordering guard, matching
    // PdfCleaner.RepairMojibake's own doc comment.
    [DataTestMethod]
    [DataRow("itâ€™s", "it's",       DisplayName = "right single quote")]
    [DataRow("â€œquoteâ€",  "\"quote\"", DisplayName = "left+right double quote")]
    [DataRow("enâ€“dash",  "en–dash",  DisplayName = "en dash")]
    [DataRow("emâ€”dash",  "em—dash",  DisplayName = "em dash")]
    [DataRow("GeÃ¯nformeerd", "Geïnformeerd", DisplayName = "i-diaeresis (Dutch)")]
    [DataRow("cliÃ«nt",   "cliënt",   DisplayName = "e-diaeresis (Dutch)")]
    [DataRow("kwaliteitÃ©n", "kwaliteitén", DisplayName = "e-acute")]
    [DataRow("bÃ¼ro",     "büro",     DisplayName = "u-diaeresis")]
    public void KnownMojibakeFragment_RoundTripsToRepairedText(string corrupted, string expected)
    {
        var cleaned = BuildCleaner().CleanPdf([Page(content: $"x {corrupted} y")]).Records[0].PageContent;

        Assert.AreEqual($"x {expected} y", cleaned, $"'{corrupted}' did not repair to '{expected}'.");
    }

    // Safety valve, decode side: 'â' alone is ambiguous - it's both the mojibake fingerprint
    // AND a legitimate letter (e.g. in loanwords). When the round-trip produces a replacement
    // char (invalid UTF-8), that means the source wasn't actually mojibake - keep it as-is.
    [TestMethod]
    public void LegitimateAccentedText_IsLeftUntouched()
    {
        var result = BuildCleaner().CleanPdf([Page(content: "vâme")]);

        Assert.AreEqual("vâme", result.Records[0].PageContent);
        Assert.AreEqual(0, result.MojibakeRepairedPages);
    }

    // Safety valve, encode side: the actual bug this class's RepairMojibake was rewritten to
    // fix. A page can contain a genuine mojibake fragment (triggering the round-trip attempt)
    // AND, elsewhere on the same page, a real character outside Windows-1252's repertoire
    // (arrows, checkboxes, non-Latin scripts). The old GetBytes(text) implementation silently
    // replaced that character with '?' and reported "repaired". EncoderExceptionFallback
    // makes that throw instead, so the whole page is left untouched rather than corrupted.
    [TestMethod]
    public void MojibakeFragmentPlusNonCp1252Character_IsLeftUntouched()
    {
        var result = BuildCleaner().CleanPdf([Page(content: "GeÃ¯nformeerd → volgende stap")]);

        Assert.AreEqual("GeÃ¯nformeerd → volgende stap", result.Records[0].PageContent);
        Assert.AreEqual(0, result.MojibakeRepairedPages);
    }
}
