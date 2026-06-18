using System.Diagnostics;
using System.Text;
using Azure.Storage.Blobs.Models;
using ProtocolsIndexer.Models;
using ProtocolsIndexer.Utils;
using UglyToad.PdfPig;

namespace ProtocolsIndexer.Services;

public class PdfPigExtractionService : IExtractionService
{
    public string Name => "PdfPig (Heuristic)";

    private static readonly HashSet<string> KnownSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "Samenvatting", "Ziekteverschijnselen", "Verwekker", "Epidemiologie",
        "Reservoir", "Besmettingsweg", "Incubatieperiode", "Besmettelijke periode",
        "Diagnostiek", "Behandeling", "Preventie", "Maatregelen", "Meldingsplicht",
        "Bronopsporing", "Contactonderzoek", "Desinfectie", "Risicogroepen",
        "Achtergrondinformatie", "Arbeidsrelevante aanvullingen", "Veterinaire informatie",
        "Literatuur"
    };

    public Task<ExtractionRun> ExtractAsync(BlobItem blob, byte[] pdfBytes, CancellationToken ct = default)
    {
        var sw  = Stopwatch.StartNew();
        var run = new ExtractionRun { ServiceName = Name, BlobName = blob.Name };

        try
        {
            using var pdf = PdfDocument.Open(pdfBytes);

            // Word.FontSize doesn't exist in PdfPig 0.1.9 — use Letters.Max as word-level proxy
            var dominantFontSize = pdf.GetPages()
                .SelectMany(p => p.GetWords())
                .Where(w => w.Letters.Any())
                .GroupBy(w => Math.Round(w.Letters.Max(l => l.FontSize), 1))
                .OrderByDescending(g => g.Count())
                .FirstOrDefault()?.Key ?? 10.0;

            // Preserve line structure so multiline regexes anchor correctly
            var firstPageText = string.Join("\n", pdf.GetPages().Take(2)
                .Select(p => string.Join("\n", p.GetWords()
                    .GroupBy(w => Math.Round(w.BoundingBox.Bottom, 1))
                    .OrderByDescending(g => g.Key)
                    .Select(g => string.Join(" ", g
                        .OrderBy(w => w.BoundingBox.Left)
                        .Select(w => w.Text))))));

            var meta = LciMetadataParser.Parse(firstPageText, blob.Name);

            string?      currentHeading = null;
            int          currentPage    = 1;
            var          buffer         = new StringBuilder();
            int          chunkIndex     = 0;
            bool         headingFound   = false;
            var          allWords       = new List<string>(); // cached for fallback

            void FlushChunk()
            {
                var text = buffer.ToString().Trim();
                if (string.IsNullOrWhiteSpace(text) || text.Split(' ').Length < 5) return;

                run.Chunks.Add(new ProtocolDocument
                {
                    Id              = $"{blob.Name}::{chunkIndex}",
                    SourceFile      = blob.Name,
                    RichtlijnName   = meta.RichtlijnName,
                    PublicationDate = meta.PublicationDate,
                    Version         = meta.Version,
                    Content         = text,
                    Heading         = currentHeading,
                    PageNumber      = currentPage,
                    ChunkIndex      = chunkIndex++
                });
                buffer.Clear();
            }

            foreach (var page in pdf.GetPages())
            {
                var pageHeight = page.MediaBox.Bounds.Height;

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
                        Page     = page.Number
                    });

                foreach (var line in lines)
                {
                    var text = line.Text.Trim();
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    allWords.AddRange(text.Split(' ', StringSplitOptions.RemoveEmptyEntries));

                    var isKnown     = KnownSections.Contains(text);
                    var isFont      = line.FontSize > dominantFontSize * 1.15 && text.Length < 80;
                    // Require bold lines to also be visually larger — filters out bold emphasis,
                    // table headers, and TOC entries that share the body font size.
                    var isBoldShort = line.IsBold && line.FontSize > dominantFontSize * 1.05
                                      && text.Split(' ').Length <= 6;

                    if (isKnown || isFont || isBoldShort)
                    {
                        FlushChunk();
                        currentHeading = text;
                        currentPage    = line.Page;
                        headingFound   = true;
                    }
                    else
                    {
                        if (buffer.Length > 0) buffer.Append(' ');
                        buffer.Append(text);
                        currentPage = line.Page; // always track current page
                    }
                }
            }

            FlushChunk();

            // Fallback: sliding-window on cached words — no PDF re-read
            if (!headingFound || run.Chunks.Count <= 1)
            {
                run.Chunks.Clear();
                chunkIndex       = 0;
                run.UsedFallback = true;

                const int windowSize = 512;
                const int overlap    = 50;

                for (int i = 0; i < allWords.Count; i += windowSize - overlap)
                {
                    var chunk = string.Join(" ", allWords.Skip(i).Take(windowSize));
                    if (string.IsNullOrWhiteSpace(chunk)) continue;

                    run.Chunks.Add(new ProtocolDocument
                    {
                        Id              = $"{blob.Name}::{chunkIndex}",
                        SourceFile      = blob.Name,
                        RichtlijnName   = meta.RichtlijnName,
                        PublicationDate = meta.PublicationDate,
                        Version         = meta.Version,
                        Content         = chunk,
                        ChunkIndex      = chunkIndex++
                    });
                }
            }
        }
        catch (Exception ex)
        {
            run.Error = ex.Message;
        }

        run.ElapsedMs = sw.ElapsedMilliseconds;
        return Task.FromResult(run);
    }
}
