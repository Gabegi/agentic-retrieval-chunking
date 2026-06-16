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

            // Dominant font size = body text baseline
            var dominantFontSize = pdf.GetPages()
                .SelectMany(p => p.GetWords())
                .SelectMany(w => w.Letters)
                .Where(l => l.FontSize > 0)
                .GroupBy(l => Math.Round(l.FontSize, 1))
                .OrderByDescending(g => g.Count())
                .FirstOrDefault()?.Key ?? 10.0;

            // Parse metadata from first two pages
            var firstPageText = string.Join(" ", pdf.GetPages().Take(2)
                .SelectMany(p => p.GetWords()).Select(w => w.Text));
            var meta = LciMetadataParser.Parse(firstPageText, blob.Name);

            string?      currentHeading = null;
            int          currentPage    = 1;
            var          buffer         = new StringBuilder();
            int          chunkIndex     = 0;
            bool         headingFound   = false;

            void FlushChunk()
            {
                var text = buffer.ToString().Trim();
                if (string.IsNullOrWhiteSpace(text)) return;

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
                var lines = page.GetWords()
                    .GroupBy(w => Math.Round(w.BoundingBox.Bottom, 1))
                    .OrderByDescending(g => g.Key)
                    .Select(g => new
                    {
                        Text     = string.Join(" ", g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)),
                        FontSize = g.SelectMany(w => w.Letters).Max(l => l.FontSize),
                        IsBold   = g.SelectMany(w => w.Letters)
                                    .Any(l => l.FontName?.Contains("Bold", StringComparison.OrdinalIgnoreCase) == true),
                        Page     = page.Number
                    });

                foreach (var line in lines)
                {
                    var text = line.Text.Trim();
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    var isKnown    = KnownSections.Contains(text);
                    var isFont     = line.FontSize > dominantFontSize * 1.15 && text.Length < 80;
                    var isBoldShort = line.IsBold && text.Split(' ').Length <= 6;

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
                        if (currentPage == 1 && line.Page > 0)
                            currentPage = line.Page;
                    }
                }
            }

            FlushChunk();

            // Fallback: sliding-window chunking if no structure detected
            if (!headingFound || run.Chunks.Count <= 1)
            {
                run.Chunks.Clear();
                chunkIndex       = 0;
                run.UsedFallback = true;

                var words = pdf.GetPages()
                    .SelectMany(p => p.GetWords().Select(w => w.Text))
                    .ToArray();

                const int windowSize = 512;
                const int overlap    = 50;

                for (int i = 0; i < words.Length; i += windowSize - overlap)
                {
                    var chunk = string.Join(" ", words.Skip(i).Take(windowSize));
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
