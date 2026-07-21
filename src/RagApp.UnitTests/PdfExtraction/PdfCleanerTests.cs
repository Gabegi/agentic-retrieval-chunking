using Microsoft.VisualStudio.TestTools.UnitTesting;
using AgenticRagApp.Models;
using AgenticRagApp.Services;

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
        PageNumber  = pageIndex,
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
    //
    // corrupted = the UTF-8 bytes of `expected`, individually reinterpreted as Windows-1252
    // codepoints. E.g. row 1: U+00EF (i-diaeresis) -> UTF-8 bytes 0xC3 0xAF -> read as cp1252
    // codepoints U+00C3 (Ã) and U+00AF (macron) -> "Ã¯". Scoped to this vowel-accent range
    // (the classic Dutch-text mojibake) rather than the smart-quote range (U+2018-U+201D) -
    // those need the exact curly-vs-straight quote character verified byte-for-byte before
    // adding as a test case, not eyeballed.
    [TestMethod]
    [DataRow("Ã¯", "ï", DisplayName = "i-diaeresis (Dutch, e.g. cliënt)")]
    [DataRow("Ã«", "ë", DisplayName = "e-diaeresis (Dutch)")]
    [DataRow("Ã©", "é", DisplayName = "e-acute")]
    [DataRow("Ã¼", "ü", DisplayName = "u-diaeresis")]
    public void KnownMojibakeFragment_RoundTripsToRepairedText(string corrupted, string expected)
    {
        var cleaned = BuildCleaner().CleanPdf([Page(content: $"x {corrupted} y")]).Records[0].PageContent;

        Assert.AreEqual($"x {expected} y", cleaned, $"'{corrupted}' did not repair to '{expected}'.");
    }

    // Safety valve, decode side: 'â' alone is ambiguous - it's both the mojibake fingerprint
    // AND a legitimate letter (e.g. in loanwords). When the round-trip produces a replacement
    // char (invalid UTF-8), that means the source wasn't actually mojibake - keep it as-is.
    // "vâme" = "vâme", a plausible genuine word fragment, not mojibake.
    [TestMethod]
    public void LegitimateAccentedText_IsLeftUntouched()
    {
        var content = "vâme";
        var result  = BuildCleaner().CleanPdf([Page(content: content)]);

        Assert.AreEqual(content, result.Records[0].PageContent);
        Assert.AreEqual(0, result.MojibakeRepairedPages);
    }

    // Safety valve, encode side: the actual bug this class's RepairMojibake was rewritten to
    // fix. A page can contain a genuine mojibake fragment (triggering the round-trip attempt)
    // AND, elsewhere on the same page, a real character outside Windows-1252's repertoire
    // (arrows, checkboxes, non-Latin scripts - here U+2192, a right arrow). The old
    // GetBytes(text) implementation silently replaced that character with '?' and reported
    // "repaired". EncoderExceptionFallback makes that throw instead, so the whole page is
    // left untouched rather than corrupted.
    [TestMethod]
    public void MojibakeFragmentPlusNonCp1252Character_IsLeftUntouched()
    {
        var content = "GeÃ¯nformeerd → volgende stap";
        var result  = BuildCleaner().CleanPdf([Page(content: content)]);

        Assert.AreEqual(content, result.Records[0].PageContent);
        Assert.AreEqual(0, result.MojibakeRepairedPages);
    }
}
