using System.Diagnostics;
using ProtocolsIndexer.Comparison.Interfaces;
using ProtocolsIndexer.Comparison.Models;
using UglyToad.PdfPig;

namespace ProtocolsIndexer.Comparison.Services;

public class PdfPigExtractionStrategy : IExtractionStrategy
{
    public string Name => "PdfPig (Structured)";

    private static readonly HashSet<string> KnownSections =
    [
        "Samenvatting", "Ziekteverschijnselen", "Verwekker", "Epidemiologie",
        "Reservoir", "Besmettingsweg", "Incubatieperiode", "Besmettelijke periode",
        "Diagnostiek", "Behandeling", "Preventie", "Maatregelen", "Meldingsplicht",
        "Bronopsporing", "Contactonderzoek", "Desinfectie", "Risicogroepen",
        "Arbeidsrelevante aanvullingen", "Veterinaire informatie", "Literatuur"
    ];

    public Task<ExtractionResult> ExtractAsync(string blobName, byte[] pdfBytes, CancellationToken ct = default)
    {
        var sw     = Stopwatch.StartNew();
        var result = new ExtractionResult { BlobName = blobName, Method = Name };

        try
        {
            using var pdf   = PdfDocument.Open(pdfBytes);
            DocumentChunk? current = null;

            foreach (var page in pdf.GetPages())
            {
                var lines = page.GetWords()
                    .GroupBy(w => Math.Round(w.BoundingBox.Bottom, 0))
                    .OrderByDescending(g => g.Key)
                    .Select(g => new
                    {
                        Text     = string.Join(" ", g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)),
                        FontSize = g.Max(w => w.FontSize),
                        Page     = page.Number
                    });

                foreach (var line in lines)
                {
                    var text = line.Text.Trim();
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    var isHeading = KnownSections.Contains(text)
                                 || (line.FontSize >= 13 && text.Length < 80);

                    if (isHeading)
                    {
                        if (current != null && !string.IsNullOrWhiteSpace(current.Content))
                            result.Chunks.Add(current);

                        current = new DocumentChunk
                        {
                            Heading    = text,
                            PageNumber = line.Page,
                            Content    = ""
                        };
                    }
                    else
                    {
                        current ??= new DocumentChunk { PageNumber = page.Number };
                        current.Content += (current.Content.Length > 0 ? " " : "") + text;
                    }
                }
            }

            if (current != null && !string.IsNullOrWhiteSpace(current.Content))
                result.Chunks.Add(current);

            // Fallback: no structure detected — one chunk per page
            if (result.Chunks.Count == 0)
            {
                foreach (var page in pdf.GetPages())
                {
                    var text = string.Join(" ", page.GetWords().Select(w => w.Text));
                    if (!string.IsNullOrWhiteSpace(text))
                        result.Chunks.Add(new DocumentChunk { PageNumber = page.Number, Content = text });
                }
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        result.ElapsedMs        = sw.ElapsedMilliseconds;
        result.EstimatedCostUsd = 0m;
        return Task.FromResult(result);
    }
}
