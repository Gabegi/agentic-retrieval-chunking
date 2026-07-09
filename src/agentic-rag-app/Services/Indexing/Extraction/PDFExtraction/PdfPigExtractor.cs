using System.Text;
using ProtocolsIndexer.Models;
using UglyToad.PdfPig;

namespace ProtocolsIndexer.Services;

// Heuristic PDF extraction backend using PdfPig — font-size/bold/known-heading
// detection ported from the comparison spike's PdfPigExtractionService, with
// chunking stripped out entirely (chunking stays downstream, in ChunkingService,
// unchanged). Produces one PdfPageRecord per PDF page with markdown-flavored
// content ("## " headings), same shape CSV's PageRecord.PageContent arrives in.
public class PdfPigExtractor : IPdfExtractor
{
    public string Name => "PdfPig";

    // Placeholder domain vocabulary from the comparison spike (built against RIVM/LCI
    // guideline PDFs) — swap for Cordaan's real section-heading vocabulary once known.
    private static readonly HashSet<string> DefaultKnownSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "Samenvatting", "Ziekteverschijnselen", "Verwekker", "Epidemiologie",
        "Reservoir", "Besmettingsweg", "Incubatieperiode", "Besmettelijke periode",
        "Diagnostiek", "Behandeling", "Preventie", "Maatregelen", "Meldingsplicht",
        "Bronopsporing", "Contactonderzoek", "Desinfectie", "Risicogroepen",
        "Achtergrondinformatie", "Arbeidsrelevante aanvullingen", "Veterinaire informatie",
        "Literatuur",
    };

    private readonly HashSet<string> _knownSections;

    public PdfPigExtractor(IEnumerable<string>? knownSections = null)
    {
        _knownSections = knownSections is null
            ? DefaultKnownSections
            : new HashSet<string>(knownSections, StringComparer.OrdinalIgnoreCase);
    }

    public PdfFileExtraction Extract(string blobName, byte[] pdfBytes)
    {
        try
        {
            using var pdf = PdfDocument.Open(pdfBytes);

            // Word.FontSize doesn't exist in PdfPig 0.1.9 — use Letters.Max as word-level proxy.
            var dominantFontSize = pdf.GetPages()
                .SelectMany(p => p.GetWords())
                .Where(w => w.Letters.Any())
                .GroupBy(w => Math.Round(w.Letters.Max(l => l.FontSize), 1))
                .OrderByDescending(g => g.Count())
                .FirstOrDefault()?.Key ?? 10.0;

            // Preserve line structure so multiline regexes anchor correctly.
            var firstPagesText = string.Join("\n", pdf.GetPages().Take(2)
                .Select(p => string.Join("\n", p.GetWords()
                    .GroupBy(w => Math.Round(w.BoundingBox.Bottom, 1))
                    .OrderByDescending(g => g.Key)
                    .Select(g => string.Join(" ", g
                        .OrderBy(w => w.BoundingBox.Left)
                        .Select(w => w.Text))))));

            var index = PdfMetadataExtraction.Parse(blobName, firstPagesText);

            var pages         = new List<PdfPageRecord>();
            string? currentHeading = null;

            foreach (var page in pdf.GetPages())
            {
                var content = ExtractPageContent(page, dominantFontSize, ref currentHeading, out var pageHadHeading);

                // Carry the most recent heading into a page whose own text doesn't start
                // with one, so every page's content still carries its section identity —
                // chunking happens per-page downstream, so this can't be recovered later.
                if (!pageHadHeading && currentHeading != null && content.Length > 0)
                    content = $"## {currentHeading}\n\n{content}";

                pages.Add(new PdfPageRecord
                {
                    BlobName    = blobName,
                    PageIndex   = page.Number,
                    PageContent = content,
                });
            }

            return new PdfFileExtraction(pages, index, Error: null);
        }
        catch (Exception ex)
        {
            return new PdfFileExtraction([], null, new ExtractionError { DocumentId = blobName, Message = ex.Message });
        }
    }

    private string ExtractPageContent(
        UglyToad.PdfPig.Content.Page page, double dominantFontSize, ref string? currentHeading, out bool pageHadHeading)
    {
        var pageHeight = page.MediaBox.Bounds.Height;
        var buffer     = new StringBuilder();
        var hadHeading = false;

        var lines = page.GetWords()
            .GroupBy(w => Math.Round(w.BoundingBox.Bottom, 1))
            .Where(g =>
            {
                var yRatio = g.Key / pageHeight;
                return yRatio > 0.05 && yRatio < 0.95; // strip page headers/footers
            })
            .OrderByDescending(g => g.Key)
            .Select(g => new
            {
                Text     = string.Join(" ", g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)),
                FontSize = g.Max(w => w.Letters.Any() ? w.Letters.Max(l => l.FontSize) : 0.0),
                IsBold   = g.SelectMany(w => w.Letters)
                            .Any(l => l.FontName?.Contains("Bold", StringComparison.OrdinalIgnoreCase) == true),
            });

        foreach (var line in lines)
        {
            var text = line.Text.Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            var isKnown     = _knownSections.Contains(text);
            var isFont      = line.FontSize > dominantFontSize * 1.15 && text.Length < 80;
            // Require bold lines to also be visually larger — filters out bold emphasis,
            // table headers, and TOC entries that share the body font size.
            var isBoldShort = line.IsBold && line.FontSize > dominantFontSize * 1.05
                              && text.Split(' ').Length <= 6;

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

        pageHadHeading = hadHeading;
        return buffer.ToString().Trim();
    }
}
