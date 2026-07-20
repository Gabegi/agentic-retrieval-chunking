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
    public void KnownMojibakePattern_IsRepairedAndCounted()
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

    // Regression guard for the actual bug: not "these strings don't repair" but "an
    // earlier-listed pattern was a proper prefix of a later-listed one" (KnownMojibakePatterns
    // is checked in order via sequential text.Replace calls, so a shorter prefix pattern
    // checked first eats the longer pattern's shared prefix before its own fix ever runs).
    // Guards future additions to the table, not just today's entries - the actual risk is
    // someone appending a new pattern in the wrong slot.
    [TestMethod]
    public void MojibakePatterns_NoEarlierPatternIsPrefixOfLaterPattern()
    {
        var patterns = PdfCleaner.KnownMojibakePatterns;

        for (int i = 0; i < patterns.Length; i++)
            for (int j = i + 1; j < patterns.Length; j++)
                Assert.IsFalse(patterns[j].Pattern.StartsWith(patterns[i].Pattern, StringComparison.Ordinal),
                    $"Pattern[{i}] '{patterns[i].Pattern}' is a prefix of Pattern[{j}] '{patterns[j].Pattern}' " +
                    "— the shorter one must be listed after the longer one, not before it.");
    }

    // Behavioral coverage for all 9 patterns, including the two the ordering bug actually
    // broke (en/em dash). Both content and expected output are derived from PdfCleaner's own
    // table, not retyped by hand - several of these characters are visually near-identical
    // (en dash vs. em dash mojibake) and easy to mistranscribe.
    [TestMethod]
    public void AllKnownMojibakePatterns_EachRepairToTheirOwnFix()
    {
        foreach (var (pattern, fix) in PdfCleaner.KnownMojibakePatterns)
        {
            var cleaned = BuildCleaner().CleanPdf([Page(content: $"x {pattern} y")]).Records[0].PageContent;

            Assert.AreEqual($"x {fix} y", cleaned, $"Pattern '{pattern}' did not repair to '{fix}'.");
        }
    }
}
