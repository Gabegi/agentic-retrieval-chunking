using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProtocolsIndexer.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Outline;

namespace ProtocolsIndexer.Services;

//
public class PdfPigExtractor : IPdfExtractor
{
    // to compare with DI
    public string Name => "PdfPig";
    // to skip pictures only pages
    private const int MinExpectedCharsPerPage = 20;
    // a line becomes header if format is 15% than page's dominant font size
    private const double HeadingFontSizeRatio     = 1.15;
    // same but for bold text
    private const double BoldHeadingFontSizeRatio = 1.05;
    // a line matched by size ratio is only captured if it's less than MaxHeadingLength
    private const int    MaxHeadingLength         = 80;
    // A bold line only becomes a heading if it's ≤6 words
    private const int    MaxBoldHeadingWords      = 6;

    // DecorationTextBlockClassifier works with more than 2 docs
    private const int MinPagesForDecorationDetection = 3;

    // sample maybe 20-30 real documents across different authors/departments and check two things:
    // (1) do headings look visually distinct (bold/larger) or do the PDFs carry bookmarks, and
    // (2) do section names actually recur across documents.
    // If (1) is true, you don't need a vocabulary at all — font-size/bold/bookmark detection already carries it, and vocabulary is just a nice-to-have on top.
    // If documents are genuinely heterogeneous with no visual distinction and no bookmarks,
    // no vocabulary size will fix that — that's a different, harder problem (ML/LLM-based heading detection, not a lookup table),
    //  and the honest move is to let DocsWithNoPagesWithHeading in the validator report tell you how big that bucket actually is before investing in solving it.
    private static readonly HashSet<string> DefaultKnownSections = new(StringComparer.OrdinalIgnoreCase)
    {
    };

    private readonly HashSet<string> _knownSections;
    private readonly ILogger<PdfPigExtractor> _logger;

    // Stateless PdfPig singletons, safe to share across calls.
    private readonly NearestNeighbourWordExtractor _wordExtractor = NearestNeighbourWordExtractor.Instance;

    // orders words the way a human would actually read them = keep meaning
    private readonly UnsupervisedReadingOrderDetector _readingOrder = UnsupervisedReadingOrderDetector.Instance;

    public PdfPigExtractor(ILogger<PdfPigExtractor>? logger = null, IEnumerable<string>? knownSections = null)
    {
        _logger        = logger ?? NullLogger<PdfPigExtractor>.Instance;
        _knownSections = knownSections is null
            ? new HashSet<string>(DefaultKnownSections, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(knownSections, StringComparer.OrdinalIgnoreCase);
    }

    public PdfFileExtraction ExtractPDF(string blobName, byte[] pdfBytes)
    {
        var errors   = new List<ExtractionError>();
        var warnings = new List<ExtractionWarning>();

        if (!TryOpenAndValidate(pdfBytes, blobName, out var pdf, out var openError))
            return Failed(blobName, openError);

        using (pdf)
        {
            var knownSections    = GetKnownSections(pdf, blobName);
            var dominantFontSize = GetDominantFontSize(pdf);

            var allPages = pdf.GetPages().ToList();

            // One segmenter, built once from the document's dominant page
            // width and reused everywhere below, so decoration detection and
            // content extraction agree on block boundaries for a given page.
            var segmenter = CreateSegmenter(GetDominantPageWidth(allPages));

            // TODO: docs below MinPagesForDecorationDetection get no header/footer
            // stripping at all — the empty dictionary below means every line on
            // every page of a 1-2 page doc is kept, decoration or not. Needs a
            // dedicated method for these docs; a naive top/bottom-of-page-% cut
            // was considered and rejected (no repetition check, so it can't tell
            // a real header from a title/opening paragraph near the page edge).
            var decorationByPage = pdf.NumberOfPages >= MinPagesForDecorationDetection
                ? GetDecorationTextByPage(allPages, segmenter, blobName)
                : new Dictionary<int, HashSet<string>>();

            var firstPagesText = BuildMetadataText(allPages.Take(2), segmenter);

            var pages              = new List<PdfPageRecord>();
            string? currentHeading = null;

            for (var pageNumber = 1; pageNumber <= pdf.NumberOfPages; pageNumber++)
            {
                try
                {
                    var page = pdf.GetPage(pageNumber);
                    decorationByPage.TryGetValue(pageNumber, out var decoration);

                    var content = ExtractPageContent(
                        page, segmenter, dominantFontSize, decoration ?? [], knownSections,
                        ref currentHeading, out var pageHadHeading);

                    // Carry the most recent heading into a page whose own text
                    // doesn't start with one — chunking is per-page downstream,
                    // so section identity can't be recovered later.
                    if (!pageHadHeading && currentHeading != null && content.Length > 0)
                        content = $"## {currentHeading}\n\n{content}";

                    if (content.Length < MinExpectedCharsPerPage)
                        warnings.Add(new ExtractionWarning
                        {
                            DocumentId = blobName,
                            RowNumber  = pageNumber,
                            Message    = $"Page {pageNumber}: only {content.Length} char(s) extracted — likely image-only (scanned) page.",
                        });

                    pages.Add(new PdfPageRecord
                    {
                        BlobName    = blobName,
                        PageIndex   = pageNumber,
                        PageContent = content,
                    });
                }
                catch (Exception ex)
                {
                    errors.Add(new ExtractionError
                    {
                        DocumentId = blobName,
                        RowNumber  = pageNumber, // reused as "page number"
                        Message    = $"Unreadable PDF page {pageNumber}: {ex.Message}",
                    });
                }
            }

            if (pages.Count == 0)
                return Failed(blobName,
                    $"All {pdf.NumberOfPages} page(s) failed extraction. First error: {errors.FirstOrDefault()?.Message}");

            var index = PdfMetadataExtraction.Parse(blobName, firstPagesText);

            return new PdfFileExtraction(pages, index, Error: null)
            {
                PageErrors = errors,
                Warnings   = warnings,
            };
        }
    }

    private static PdfFileExtraction Failed(string blobName, string message) =>
        new([], null, new ExtractionError { DocumentId = blobName, Message = message });

    // Step 1: open the PDF and confirm it's usable — a real, parseable PDF with at
    // least one page. `pdf`/`error` are [NotNullWhen]-annotated against the bool
    // return so the caller doesn't need a null-forgiving `!` to use either one —
    // the compiler enforces the "exactly one of these is non-null" invariant
    // instead of it just being a convention. Caller owns disposal of `pdf` on
    // success (true).
    private bool TryOpenAndValidate(
        byte[] pdfBytes, string blobName,
        [NotNullWhen(true)]  out PdfDocument? pdf,
        [NotNullWhen(false)] out string?      error)
    {
        // `opened` is a plain local, not the out parameter — lets the catch block
        // read it safely (out parameters can't be read before they're definitely
        // assigned) to dispose a document that opened fine but failed a later
        // check in this same try, without leaking it.
        PdfDocument? opened = null;
        try
        {
            opened = PdfDocument.Open(pdfBytes);

            if (opened.NumberOfPages == 0)
            {
                opened.Dispose();
                pdf   = null;
                error = "PDF contains zero pages.";
                return false;
            }

            _logger.LogInformation(
                "Opened PDF '{Blob}': {Pages} page(s), version {Version}",
                blobName, opened.NumberOfPages, opened.Version);

            pdf   = opened;
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            opened?.Dispose();
            pdf   = null;
            error = $"Not a parseable PDF: {ex.Message}";
            return false;
        }
    }

    private static double GetDominantFontSize(PdfDocument pdf) =>
        pdf.GetPages()
            .SelectMany(p => p.GetWords())
            .Where(w => w.Letters.Any())
            .GroupBy(w => Math.Round(w.Letters.Max(l => l.FontSize), 1))
            .OrderByDescending(g => g.Count())
            .FirstOrDefault()?.Key ?? 10.0;

    private static double GetDominantPageWidth(IReadOnlyList<Page> pages) =>
        pages
            .GroupBy(p => Math.Round(p.MediaBox.Bounds.Width, 1))
            .OrderByDescending(g => g.Count())
            .First().Key;

    // Bookmarks (the PDF Outline pane) are the closest thing to an
    // authoritative heading list a PDF can carry — when the source Word doc
    // used heading styles and "create bookmarks" on export, this is the
    // actual document ToC, not a heuristic guess. Known to be unreliable
    // though (PdfPig issue #736: TryGetBookmarks can return true with zero
    // entries depending on how the PDF's Outlines dictionary was built), so
    // this only ever *adds* candidate headings on top of the hardcoded
    // list, never replaces the font-size/bold detection below.
    private HashSet<string> GetKnownSections(PdfDocument pdf, string blobName)
    {
        if (!pdf.TryGetBookmarks(out var bookmarks) || bookmarks.Roots.Count == 0)
            return _knownSections;

        var fromBookmarks = CollectBookmarkTitles(bookmarks.Roots).ToList();
        if (fromBookmarks.Count == 0)
            return _knownSections;

        _logger.LogInformation(
            "PDF '{Blob}': added {Count} heading(s) from document bookmarks.", blobName, fromBookmarks.Count);

        var merged = new HashSet<string>(_knownSections, StringComparer.OrdinalIgnoreCase);
        merged.UnionWith(fromBookmarks);
        return merged;
    }

    private static IEnumerable<string> CollectBookmarkTitles(IEnumerable<BookmarkNode> nodes)
    {
        foreach (var node in nodes)
        {
            var title = node.Title?.Trim();
            if (!string.IsNullOrWhiteSpace(title) && title.Length < MaxHeadingLength)
                yield return title;

            foreach (var childTitle in CollectBookmarkTitles(node.Children))
                yield return childTitle;
        }
    }

    // Finds blocks repeated near-verbatim across pages (running titles, page
    // numbers, confidentiality notices) via the same word/segment pipeline
    // used for content below, then hands back one decoration set per page.
    // Matched by trimmed block text rather than object reference, since
    // content extraction re-segments each page independently — the two
    // passes agree because they're handed the exact same segmenter
    // instance, not just an equivalently-configured one.
    private Dictionary<int, HashSet<string>> GetDecorationTextByPage(
        IReadOnlyList<Page> allPages, RecursiveXYCut segmenter, string blobName)
    {
        var result = new Dictionary<int, HashSet<string>>();
        try
        {
            var perPage = DecorationTextBlockClassifier.Get(allPages, _wordExtractor, segmenter);

            for (var i = 0; i < perPage.Count; i++)
                result[i + 1] = perPage[i].Select(b => b.Text.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // Decoration detection is a quality improvement, not a
            // correctness requirement — degrade to "no header/footer
            // stripping" instead of failing the whole file over it.
            _logger.LogWarning(ex, "Decoration (header/footer) detection failed for '{Blob}'; continuing without it.", blobName);
        }
        return result;
    }

    // Built separately from the per-page PageContent below, on purpose:
    // PdfMetadataExtraction.TitleRegex is ^(.+?)\s*\|\s*LCI-richtlijn with
    // RegexOptions.Multiline, which needs the title sitting on its own
    // physical line to match ^. PageContent joins lines within a block with
    // plain spaces (see ExtractPageContent), which would merge a title into
    // its surrounding paragraph and silently break that anchor — title
    // extraction would fall back to the filename guess with no error. This
    // keeps one output line per PDF line instead, using the same word/block/
    // reading-order pipeline as the rest of the file (more robust than the
    // original spike's raw Y-grouping on a multi-column first page, same
    // one-line-per-line property that regex needs).
    private string BuildMetadataText(IEnumerable<Page> pages, RecursiveXYCut segmenter)
    {
        try
        {
            var pageTexts = pages.Select(page =>
            {
                var words   = _wordExtractor.GetWords(page.Letters);
                var blocks  = segmenter.GetBlocks(words);
                var ordered = _readingOrder.Get(blocks);

                var lines = ordered.SelectMany(b => b.TextLines)
                    .Select(l => string.Join(" ", l.Words.Select(w => w.Text)).Trim())
                    .Where(l => l.Length > 0);

                return string.Join("\n", lines);
            });

            return string.Join("\n\n", pageTexts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not build metadata text; title/version extraction may fall back to defaults.");
            return "";
        }
    }

    // Isolated bullet points can otherwise get cut into their own spurious
    // column by RecursiveXYCut; per the wiki's own tuning note, a minimum
    // block width of ~1/3 page width avoids that without blocking genuine
    // column splits.
    private static RecursiveXYCut CreateSegmenter(double pageWidth) =>
        new(new RecursiveXYCut.RecursiveXYCutOptions { MinimumWidth = pageWidth / 3 });

    // Rebuilds reading order geometrically instead of by raw Y-coordinate:
    //   1. Words (NearestNeighbourWordExtractor) — connects glyphs by
    //      proximity, independent of the order the PDF drew them in.
    //   2. Blocks (segmenter, shared across the document — see Extract) —
    //      cuts on column gaps, so a 2-column page yields separate column
    //      blocks instead of one interleaved mess.
    //   3. Reading order (UnsupervisedReadingOrderDetector) — walks blocks
    //      the way a person reads them: column A fully, then column B.
    // Heading classification runs per TextLine within each ordered block,
    // using the same known-vocabulary / font-size / bold signals as before.
    // Tables aren't extracted separately here — a table's cells get read as
    // ordinary text lines, in reading order, same as everything else on the
    // page (see conversation/decision to drop Tabula-based extraction).
    private string ExtractPageContent(
        Page page, RecursiveXYCut segmenter, double dominantFontSize, HashSet<string> decoration, HashSet<string> knownSections,
        ref string? currentHeading, out bool pageHadHeading)
    {
        var buffer     = new StringBuilder();
        var hadHeading = false;

        var words   = _wordExtractor.GetWords(page.Letters);
        var blocks  = segmenter.GetBlocks(words);
        var ordered = _readingOrder.Get(blocks);

        foreach (var block in ordered)
        {
            if (decoration.Contains(block.Text.Trim())) continue; // running header/footer

            foreach (var line in block.TextLines)
            {
                var text = string.Join(" ", line.Words.Select(w => w.Text)).Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;

                var letters  = line.Words.SelectMany(w => w.Letters).ToList();
                var fontSize = letters.Count > 0 ? letters.Max(l => l.FontSize) : dominantFontSize;
                var isBold   = letters.Any(l => l.FontName?.Contains("Bold", StringComparison.OrdinalIgnoreCase) == true);

                var isKnown     = knownSections.Contains(text);
                var isFont      = fontSize > dominantFontSize * HeadingFontSizeRatio && text.Length < MaxHeadingLength;
                var isBoldShort = isBold && fontSize > dominantFontSize * BoldHeadingFontSizeRatio
                                  && text.Split(' ').Length <= MaxBoldHeadingWords;

                if (isKnown || isFont || isBoldShort)
                {
                    currentHeading = text;
                    hadHeading     = true;
                    buffer.Append(buffer.Length > 0 ? "\n\n" : "").Append("## ").Append(text);
                }
                else
                {
                    buffer.Append(buffer.Length > 0 ? " " : "").Append(text);
                }
            }
        }

        pageHadHeading = hadHeading;
        return buffer.ToString().Trim();
    }
}
