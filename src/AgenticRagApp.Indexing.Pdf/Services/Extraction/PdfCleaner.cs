using System.Text;
using System.Text.RegularExpressions;
using AgenticRagApp.Indexing.Pdf.Models;
using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Cleans extracted PDF page records for RAG indexing.
//
// End goal - every transform must either:
//   - repair extraction damage (mojibake, ligatures, broken hyphenation), or
//   - strip characters that add embedding/search noise without carrying meaning
//     (control chars, zero-width chars, whitespace debris).
// Nothing here rewrites or paraphrases actual content - chunking and retrieval need
// the source text, just undamaged. One bad page becomes a CleaningError; it never
// aborts the whole run.
//
// Explicitly out of scope:
//   - Duplicate (BlobName, PageNumber) pages - that's an extractor invariant
//     violation, asserted once in PdfPipelineValidator, not here.
//   - Header/footer/boilerplate stripping - Cordaan's PDF conventions aren't
//     confirmed yet, and a wrong regex here silently deletes real content, which is
//     worse for RAG than leaving a repeated footer in. Add once real sample PDFs
//     confirm the patterns. Document Intelligence can already exclude
//     pageHeader/pageFooter roles at extraction time - prefer solving it there.
public class PdfCleaner : IPdfCleaner
{
    // Windows-1252 with exception fallbacks on both sides: any character that can't
    // round-trip losslessly throws instead of silently becoming '?' (encode) or a
    // replacement char (decode). RepairMojibake treats either as "not mojibake" and
    // keeps the original text - for RAG, an unrepaired page beats a corrupted one.
    // Requires Encoding.RegisterProvider(CodePagesEncodingProvider.Instance) at
    // startup (see program.cs) and the System.Text.Encoding.CodePages package.
    private static readonly Encoding Win1252Strict = Encoding.GetEncoding(
        1252, new EncoderExceptionFallback(), new DecoderExceptionFallback());

    // Collapse 3+ consecutive newlines down to a single blank line.
    private static readonly Regex ExcessBlankLines = new(@"\n{3,}", RegexOptions.Compiled);

    // Runs of spaces/tabs -> single space. PDF text extraction frequently emits
    // alignment gaps as multiple spaces; they carry layout, not meaning, and waste
    // embedding tokens.
    private static readonly Regex ExcessSpaces = new(@"[ \t]{2,}", RegexOptions.Compiled);

    // Trailing whitespace before a newline - pure noise from line-based extraction.
    private static readonly Regex TrailingLineSpace = new(@"[ \t]+\n", RegexOptions.Compiled);

    // A word split across a line break by end-of-line hyphenation:
    // "informa-\ntie" -> "informatie". Requires lowercase letters on BOTH sides so
    // legitimate hyphenated compounds at a line break ("ADL-ondersteuning") and
    // list-dash lines are left alone. Matters for RAG: a split word matches neither
    // the query embedding nor keyword search.
    private static readonly Regex LineBreakHyphenation =
        new(@"(?<=\p{Ll})-\n(?=\p{Ll})", RegexOptions.Compiled);

    // Control chars except \n and \t. PDF extractors leak these (form feeds, vertical
    // tabs, stray NULs) and they poison both embeddings and JSON payloads downstream.
    private static readonly Regex ControlChars =
        new(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]", RegexOptions.Compiled);

    // Invisible characters that break tokenization and exact-match retrieval while
    // rendering as nothing: zero-width space/joiner/non-joiner, BOM, soft hyphen.
    private static readonly Regex InvisibleChars =
        new(@"[\u200B\u200C\u200D\uFEFF\u00AD]", RegexOptions.Compiled);

    // Typographic ligatures PDFs embed as single glyphs (e.g. U+FB01 "fi") that won't
    // match a plain-text query in keyword/hybrid search - expand to plain letters.
    // internal: PdfCleanerTests asserts table completeness directly against this.
    internal static readonly (string Ligature, string Expansion)[] LigatureExpansions =
    [
        ("\uFB01", "fi"), ("\uFB02", "fl"), ("\uFB00", "ff"), ("\uFB03", "ffi"), ("\uFB04", "ffl"),
    ];

    public PdfCleanResult CleanPdf(IReadOnlyList<PdfPageRecord> pages)
    {
        var result = new PdfCleanResult();

        foreach (var page in pages)
            CleanSinglePage(page, result);

        return result;
    }

    private static void CleanSinglePage(PdfPageRecord page, PdfCleanResult result)
    {
        try
        {
            var (content, mojibakeFixed) = CleanPageContent(page.PageContent ?? "");

            if (mojibakeFixed)
            {
                result.CountMojibakeRepaired();
                result.AddWarning(new CleaningWarning(
                    DocumentId: page.BlobName,
                    Message:    $"Page {page.PageNumber}: repaired mojibake in source text (round-trip re-decode)."));
            }

            if (string.IsNullOrWhiteSpace(content))
                result.AddWarning(new CleaningWarning(
                    DocumentId: page.BlobName,
                    Message:    $"PageContent is empty after cleanup (page {page.PageNumber}) - likely a blank source page."));

            result.AddRecord(ToCleanedRecord(page, content));
        }
        catch (Exception ex)
        {
            result.AddError(new CleaningError(DocumentId: page.BlobName, Message: ex.Message));
        }
    }

    private static CleanedPdfPageRecord ToCleanedRecord(PdfPageRecord page, string content) => new()
    {
        BlobName    = page.BlobName,
        PageNumber  = page.PageNumber,
        PageContent = content,
        Title       = TrimOrEmpty(page.Title),
    };

    private static string TrimOrEmpty(string? value) => value?.Trim() ?? "";

    // Cleanup order, deliberate:
    //   1. Line endings first - every later regex only has to reason about \n.
    //   2. Mojibake repair before anything else that inspects characters -
    //      downstream steps should see the *real* text.
    //   3. Character-level cleanup: control/invisible chars, ligatures, NBSP.
    //   4. NFC normalization - accented letters are always one codepoint; composed
    //      vs. decomposed forms embed and keyword-match differently, which is silent
    //      retrieval noise.
    //   5. Hyphenation repair before whitespace collapse - it consumes a \n.
    //   6. Whitespace last, over the fully repaired text.
    private static (string Content, bool MojibakeFixed) CleanPageContent(string raw)
    {
        var text = raw.Replace("\r\n", "\n").Replace("\r", "\n");

        (text, var mojibakeFixed) = RepairMojibake(text);

        text = ControlChars.Replace(text, "");
        text = InvisibleChars.Replace(text, "");
        foreach (var (ligature, expansion) in LigatureExpansions)
            text = text.Replace(ligature, expansion);
        text = text.Replace('\u00A0', ' '); // NBSP -> plain space

        text = text.Normalize(NormalizationForm.FormC);

        text = LineBreakHyphenation.Replace(text, "");

        text = TrailingLineSpace.Replace(text, "\n");
        text = ExcessSpaces.Replace(text, " ");
        text = ExcessBlankLines.Replace(text, "\n\n");

        return (text.Trim(), mojibakeFixed);
    }

    // Repairs the entire Windows-1252/UTF-8 mis-decode class in one round-trip
    // instead of enumerating symptoms pattern by pattern. Signature-gated so clean
    // text (the overwhelmingly common case with Document Intelligence) skips the
    // re-decode entirely.
    private static (string Text, bool Fixed) RepairMojibake(string text)
    {
        // U+00C3 and U+00E2 are the fingerprint of UTF-8 bytes read as Windows-1252.
        if (!text.Contains('\u00C3') && !text.Contains('\u00E2'))
            return (text, false);

        try
        {
            var repaired = Encoding.UTF8.GetString(Win1252Strict.GetBytes(text));

            // U+FFFD means the round-trip failed - the text was legitimate (e.g. a
            // genuine U+00E2 in a loanword), not mojibake.
            return repaired.Contains('\uFFFD') ? (text, false) : (repaired, true);
        }
        catch (EncoderFallbackException)
        {
            // text has characters outside Windows-1252 (arrows, checkboxes, non-Latin
            // scripts, etc.) - genuine content, not mojibake. Leave it untouched.
            return (text, false);
        }
    }
}
