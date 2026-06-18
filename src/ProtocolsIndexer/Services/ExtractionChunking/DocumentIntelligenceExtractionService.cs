using System.Diagnostics;
using System.Text;
using Azure;
using Azure.AI.DocumentIntelligence;
using ProtocolsIndexer.Models;
using ProtocolsIndexer.Utils;

namespace ProtocolsIndexer.Services;

public class DocumentIntelligenceExtractionService : IExtractionService
{
    public string Name => "Document Intelligence";

    private readonly DocumentIntelligenceClient _client;
    private const decimal CostPerPage = 0.001m;

    public DocumentIntelligenceExtractionService(DocumentIntelligenceClient client)
    {
        _client = client;
    }

    public async Task<ExtractionRun> ExtractAsync(string blobName, byte[] pdfBytes, CancellationToken ct = default)
    {
        var sw  = Stopwatch.StartNew();
        var run = new ExtractionRun { ServiceName = Name, BlobName = blobName };

        try
        {
            var operation = await _client.AnalyzeDocumentAsync(
                WaitUntil.Completed, "prebuilt-layout", BinaryData.FromBytes(pdfBytes),
                cancellationToken: ct);

            var analysis  = operation.Value;
            var pageCount = analysis.Pages?.Count ?? 0;

            // Preserve newlines so multiline regexes in LciMetadataParser anchor correctly
            var firstPagesText = string.Join("\n",
                analysis.Paragraphs?
                    .Where(p => (p.BoundingRegions is { Count: > 0 } br0 ? br0[0].PageNumber : 0) <= 2)
                    .Select(p => p.Content) ?? []);

            var meta       = LciMetadataParser.Parse(firstPagesText, blobName);
            int chunkIndex = 0;
            ProtocolDocument? current = null;

            void FlushChunk()
            {
                if (current == null
                    || string.IsNullOrWhiteSpace(current.Content)
                    || current.Content.Split(' ').Length < 5) return;

                foreach (var part in SplitContent(current.Content))
                {
                    run.Chunks.Add(new ProtocolDocument
                    {
                        Id              = SafeKey(blobName, chunkIndex),
                        SourceFile      = current.SourceFile,
                        RichtlijnName   = current.RichtlijnName,
                        PublicationDate = current.PublicationDate,
                        Version         = current.Version,
                        PageNumber      = current.PageNumber,
                        Heading         = current.Heading,
                        Content         = part,
                        ChunkIndex      = chunkIndex++
                    });
                }
                current = null;
            }

            ProtocolDocument BaseDoc(int pageNum) => new()
            {
                Id              = "",
                SourceFile      = blobName,
                RichtlijnName   = meta.RichtlijnName,
                PublicationDate = meta.PublicationDate,
                Version         = meta.Version,
                PageNumber      = pageNum,
                Content         = ""
            };

            static float TopY(IReadOnlyList<BoundingRegion>? regions) =>
                regions is { Count: > 0 } r && r[0].Polygon is { Count: >= 2 } p ? p[1] : 0f;

            // Merge paragraphs and tables into one stream ordered by page then Y position
            // so tables are inserted at their actual document position, not appended after all paragraphs.
            var paragraphItems = (analysis.Paragraphs ?? [])
                .Select(p => (
                    Page:  p.BoundingRegions is { Count: > 0 } br ? br[0].PageNumber : 0,
                    Y:     TopY(p.BoundingRegions),
                    Para:  (DocumentParagraph?)p,
                    Table: (DocumentTable?)null));

            var tableItems = (analysis.Tables ?? [])
                .Select(t => (
                    Page:  t.BoundingRegions is { Count: > 0 } br ? br[0].PageNumber : 0,
                    Y:     TopY(t.BoundingRegions),
                    Para:  (DocumentParagraph?)null,
                    Table: (DocumentTable?)t));

            foreach (var item in paragraphItems.Concat(tableItems).OrderBy(i => i.Page).ThenBy(i => i.Y))
            {
                if (item.Para is { } para)
                {
                    if (para.Role == ParagraphRole.PageHeader
                        || para.Role == ParagraphRole.PageFooter
                        || para.Role == ParagraphRole.PageNumber)
                        continue;

                    var pageNum = para.BoundingRegions is { Count: > 0 } brP ? brP[0].PageNumber : 0;

                    if (para.Role == ParagraphRole.Title || para.Role == ParagraphRole.SectionHeading)
                    {
                        FlushChunk();
                        current         = BaseDoc(pageNum);
                        current.Heading = para.Content;
                    }
                    else
                    {
                        current ??= BaseDoc(pageNum);
                        current.Content += (current.Content.Length > 0 ? " " : "") + para.Content;
                    }
                }
                else if (item.Table is { } table)
                {
                    var pageNum   = table.BoundingRegions is { Count: > 0 } brT ? brT[0].PageNumber : 0;
                    var tableText = string.Join(" | ", table.Cells
                        .OrderBy(c => c.RowIndex).ThenBy(c => c.ColumnIndex)
                        .Select(c => c.Content.Trim())
                        .Where(c => !string.IsNullOrWhiteSpace(c)));

                    if (string.IsNullOrWhiteSpace(tableText)) continue;

                    current ??= BaseDoc(pageNum);
                    current.Content += (current.Content.Length > 0 ? " " : "") + tableText;
                }
            }

            FlushChunk();

            run.EstimatedCostUsd = pageCount * CostPerPage;
        }
        catch (Exception ex)
        {
            run.Error = ex.Message;
        }

        run.ElapsedMs = sw.ElapsedMilliseconds;
        return run;
    }

    private static string SafeKey(string blobName, int index) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{blobName}::{index}"))
            .Replace('+', '-').Replace('/', '_');

    private static IEnumerable<string> SplitContent(string text, int maxChars = 6_000)
    {
        if (text.Length <= maxChars) { yield return text; yield break; }

        int start = 0;
        while (start < text.Length)
        {
            if (start + maxChars >= text.Length) { yield return text[start..].Trim(); yield break; }

            int end   = start + maxChars;
            int split = -1;

            for (int i = end; i > start + maxChars / 2; i--)
            {
                if (text[i] is '.' or '!' or '?' && i + 1 < text.Length && text[i + 1] == ' ')
                { split = i + 1; break; }
            }

            if (split == -1)
                for (int i = end; i > start + maxChars / 2; i--)
                    if (text[i] == ' ') { split = i + 1; break; }

            if (split == -1) split = end;

            var part = text[start..split].Trim();
            if (!string.IsNullOrWhiteSpace(part)) yield return part;
            start = split;
        }
    }
}
