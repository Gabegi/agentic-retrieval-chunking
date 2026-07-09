using System.Text;
using Azure;
using Azure.AI.DocumentIntelligence;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// Azure Document Intelligence ("prebuilt-layout") PDF extraction backend, ported
// from the comparison spike's DocumentIntelligenceExtractionService, with chunking
// stripped out entirely (chunking stays downstream, in ChunkingService, unchanged).
// Produces one PdfPageRecord per PDF page with markdown-flavored content
// ("## " headings, real column-aware pipe tables), same shape CSV's
// PageRecord.PageContent arrives in.
public class DocumentIntelligenceExtractor : IPdfExtractor
{
    public string Name => "DocumentIntelligence";

    private readonly DocumentIntelligenceClient _client;
    private const decimal CostPerPage = 0.001m;

    public DocumentIntelligenceExtractor(DocumentIntelligenceClient client)
    {
        _client = client;
    }

    public PdfFileExtraction Extract(string blobName, byte[] pdfBytes)
    {
        try
        {
            var operation = _client.AnalyzeDocument(
                WaitUntil.Completed, "prebuilt-layout", BinaryData.FromBytes(pdfBytes));

            var analysis  = operation.Value;
            var pageCount = analysis.Pages?.Count ?? 0;

            // Preserve newlines so multiline regexes in PdfMetadataExtraction anchor correctly.
            var firstPagesText = string.Join("\n",
                analysis.Paragraphs?
                    .Where(p => (p.BoundingRegions is { Count: > 0 } br0 ? br0[0].PageNumber : 0) <= 2)
                    .Select(p => p.Content) ?? []);

            var index = PdfMetadataExtraction.Parse(blobName, firstPagesText);

            var pages = BuildPageRecords(blobName, analysis, pageCount);

            return new PdfFileExtraction(pages, index, Error: null, EstimatedCostUsd: pageCount * CostPerPage);
        }
        catch (Exception ex)
        {
            return new PdfFileExtraction([], null, new ExtractionError { DocumentId = blobName, Message = ex.Message });
        }
    }

    private static List<PdfPageRecord> BuildPageRecords(string blobName, AnalyzeResult analysis, int pageCount)
    {
        var pageSegments     = new Dictionary<int, List<string>>();
        var paragraphBuffers = new Dictionary<int, StringBuilder>();

        List<string> Segments(int pageNum) =>
            pageSegments.TryGetValue(pageNum, out var l) ? l : pageSegments[pageNum] = [];
        StringBuilder ParaBuffer(int pageNum) =>
            paragraphBuffers.TryGetValue(pageNum, out var b) ? b : paragraphBuffers[pageNum] = new StringBuilder();

        void FlushParagraph(int pageNum)
        {
            if (paragraphBuffers.TryGetValue(pageNum, out var buf) && buf.Length > 0)
            {
                Segments(pageNum).Add(buf.ToString());
                buf.Clear();
            }
        }

        static float TopY(IReadOnlyList<BoundingRegion>? regions) =>
            regions is { Count: > 0 } r && r[0].Polygon is { Count: >= 2 } p ? p[1] : 0f;

        // Merge paragraphs and tables into one stream ordered by page then Y position
        // so tables are inserted at their actual document position, not appended after
        // all paragraphs.
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
                    FlushParagraph(pageNum);
                    Segments(pageNum).Add($"## {para.Content}");
                }
                else
                {
                    var buf = ParaBuffer(pageNum);
                    buf.Append(buf.Length > 0 ? " " : "").Append(para.Content);
                }
            }
            else if (item.Table is { } table)
            {
                var pageNum = table.BoundingRegions is { Count: > 0 } brT ? brT[0].PageNumber : 0;
                FlushParagraph(pageNum);

                var markdown = RenderMarkdownTable(table);
                if (!string.IsNullOrWhiteSpace(markdown))
                    Segments(pageNum).Add(markdown);
            }
        }

        foreach (var pageNum in paragraphBuffers.Keys.ToList())
            FlushParagraph(pageNum);

        // Second pass: carry the most recent heading into a page whose own content
        // doesn't start with one, so every page still carries its section identity —
        // chunking happens per-page downstream, so this can't be recovered later.
        var pages        = new List<PdfPageRecord>();
        string? carryHeading = null;

        for (var pageNum = 1; pageNum <= pageCount; pageNum++)
        {
            var segments = pageSegments.TryGetValue(pageNum, out var s) ? s : [];

            if (segments.Count > 0 && !segments[0].StartsWith("## ") && carryHeading != null)
                segments = [carryHeading, .. segments];

            var lastHeadingOnPage = segments.LastOrDefault(s2 => s2.StartsWith("## "));
            if (lastHeadingOnPage != null) carryHeading = lastHeadingOnPage;

            pages.Add(new PdfPageRecord
            {
                BlobName    = blobName,
                PageIndex   = pageNum,
                PageContent = string.Join("\n\n", segments),
            });
        }

        return pages;
    }

    // Column-aware markdown table rendering using Document Intelligence's row/column
    // indices — a real pipe table with a header row, unlike the comparison spike's
    // flat " | " join of every cell in reading order.
    private static string RenderMarkdownTable(DocumentTable table)
    {
        if (table.RowCount == 0 || table.ColumnCount == 0) return "";

        var grid = new string?[table.RowCount, table.ColumnCount];
        foreach (var cell in table.Cells)
            if (cell.RowIndex < table.RowCount && cell.ColumnIndex < table.ColumnCount)
                grid[cell.RowIndex, cell.ColumnIndex] = cell.Content?.Trim();

        var sb = new StringBuilder();
        for (var r = 0; r < table.RowCount; r++)
        {
            sb.Append('|');
            for (var c = 0; c < table.ColumnCount; c++)
                sb.Append(' ').Append(grid[r, c] ?? "").Append(" |");
            sb.Append('\n');

            if (r == 0)
            {
                sb.Append('|');
                for (var c = 0; c < table.ColumnCount; c++)
                    sb.Append(" --- |");
                sb.Append('\n');
            }
        }

        return sb.ToString().TrimEnd();
    }
}
