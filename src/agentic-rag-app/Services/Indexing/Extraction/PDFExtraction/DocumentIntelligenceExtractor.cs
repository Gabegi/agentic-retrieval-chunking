using System.Diagnostics.CodeAnalysis;
using System.Text;
using Azure;
using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    // prebuilt-layout is billed at $10 / 1,000 pages. Verify against current
    // Azure pricing before trusting cost estimates derived from this constant.
    private const decimal CostPerPage = 0.01m;

    

    private readonly ILogger<DocumentIntelligenceExtractor> _logger;
    private readonly PdfDocumentOpener                       _opener;

    public DocumentIntelligenceExtractor(DocumentIntelligenceClient client, ILogger<DocumentIntelligenceExtractor>? logger = null)
    {
        _client = client;
        _logger = logger ?? NullLogger<DocumentIntelligenceExtractor>.Instance;
        _opener = new PdfDocumentOpener(_logger);
    }


    public PdfFileExtraction ExtractPDF(string blobName, byte[] pdfBytes)
    {
        // Step 1: local, free structural check — rejects oversized/corrupt/encrypted/
        // too-many-page files before spending a paid Document Intelligence call on them.
        if (!IsPDFValid(blobName, pdfBytes, out var meta, out var checkError))
            return new PdfFileExtraction([], null, checkError);

        // Native PDF metadata is a secondary signal alongside PdfMetadataExtraction's
        // blob-name/content-derived Title/Version — not yet wired into the pipeline's
        // output (see PdfPreFlight/DocMetadata), just surfaced here for now.
        _logger.LogDebug(
            "PdfPreFlight: '{Blob}' — {Pages} page(s), title={Title}, author={Author}, created={Created}",
            blobName, meta.PageCount, meta.Title, meta.Author, meta.CreatedAt);

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

            var pages = BuildPageRecords(blobName, analysis, pageCount, _logger);

            return new PdfFileExtraction(pages, index, Error: null, EstimatedCostUsd: pageCount * CostPerPage);
        }
        catch (Exception ex)
        {
            return new PdfFileExtraction([], null, new ExtractionError { DocumentId = blobName, Message = ex.Message });
        }
    }

    // Step 1: local, free precheck before the paid Document Intelligence call. Runs
    // PdfPreFlight.IsPDFSizeOkForDI, then PdfDocumentOpener (structural open/validate —
    // encrypted/corrupt/malformed), then PdfPreFlight.IsPDFPageCountOkForDI (zero pages,
    // too many pages) on the now-open document. Each stage can reject on its own,
    // cheapest first, so a too-large file never gets opened and an unopenable file never
    // gets page-counted. Metadata is read last, only once every check has passed.
    private bool IsPDFValid(
        string blobName, byte[] pdfBytes,
        [NotNullWhen(true)]  out DocMetadata?    meta,
        [NotNullWhen(false)] out ExtractionError? error)
    {
        meta = null;

        if (!PdfPreFlight.IsPDFSizeOkForDI(pdfBytes, blobName, out error))
            return false;

        if (!_opener.TryOpenAndValidate(pdfBytes, blobName, out var pdf, out error))
            return false;

        using (pdf)
        {
            if (!PdfPreFlight.IsPDFPageCountOkForDI(pdf, blobName, out error))
                return false;

            meta = PdfMetadataExtraction.ParseNativeMetadata(pdf);
            return true;
        }
    }

    private static List<PdfPageRecord> BuildPageRecords(string blobName, AnalyzeResult analysis, int pageCount, ILogger logger)
    {
        var pageSegments = new Dictionary<int, List<string>>();

        List<string> Segments(int pageNum) =>
            pageSegments.TryGetValue(pageNum, out var l) ? l : pageSegments[pageNum] = [];

        // DI repeats every table cell's text as its own entry in analysis.Paragraphs,
        // so without filtering, table content is emitted twice: once as jumbled prose,
        // once as the markdown table below. Build the set of spans covered by tables
        // up front and drop any paragraph whose span falls inside one.
        var tableSpanRanges = (analysis.Tables ?? [])
            .SelectMany(t => t.Spans ?? [])
            .Select(sp => (Start: sp.Offset, End: sp.Offset + sp.Length))
            .ToList();

        bool OverlapsTable(IReadOnlyList<DocumentSpan>? spans) =>
            spans != null && spans.Any(sp => tableSpanRanges.Any(r => sp.Offset < r.End && sp.Offset + sp.Length > r.Start));

        // analysis.Paragraphs is already in reading order -- DI resolves multi-column
        // layouts etc. -- so it's kept as-is rather than re-sorted by Y, which would
        // interleave left/right columns on two-column pages. Tables are positioned by
        // comparing span offsets against paragraph offsets instead.
        var tablesByOffset = (analysis.Tables ?? [])
            .Select(t => (Table: t, Offset: t.Spans is { Count: > 0 } ts ? ts[0].Offset : 0))
            .OrderBy(t => t.Offset)
            .ToList();
        var nextTableIndex = 0;

        void EmitTable(DocumentTable table)
        {
            var pageNum = table.BoundingRegions is { Count: > 0 } br ? br[0].PageNumber : 0;
            if (pageNum == 0)
            {
                logger.LogWarning("DocumentIntelligence: table with no bounding region dropped in {BlobName}", blobName);
                return;
            }

            var markdown = RenderMarkdownTable(table);
            if (!string.IsNullOrWhiteSpace(markdown))
                Segments(pageNum).Add(markdown);
        }

        foreach (var para in analysis.Paragraphs ?? [])
        {
            if (para.Role == ParagraphRole.PageHeader
                || para.Role == ParagraphRole.PageFooter
                || para.Role == ParagraphRole.PageNumber)
                continue;

            if (OverlapsTable(para.Spans))
                continue; // already rendered as part of its table's markdown

            var paraOffset = para.Spans is { Count: > 0 } ps ? ps[0].Offset : 0;
            while (nextTableIndex < tablesByOffset.Count && tablesByOffset[nextTableIndex].Offset < paraOffset)
                EmitTable(tablesByOffset[nextTableIndex++].Table);

            var pageNum = para.BoundingRegions is { Count: > 0 } brP ? brP[0].PageNumber : 0;
            if (pageNum == 0)
            {
                logger.LogWarning("DocumentIntelligence: paragraph with no bounding region dropped in {BlobName}", blobName);
                continue;
            }

            if (para.Role == ParagraphRole.Title)
                Segments(pageNum).Add($"# {para.Content}");
            else if (para.Role == ParagraphRole.SectionHeading)
                Segments(pageNum).Add($"## {para.Content}");
            else
                Segments(pageNum).Add(para.Content);
        }

        while (nextTableIndex < tablesByOffset.Count)
            EmitTable(tablesByOffset[nextTableIndex++].Table);

        // Second pass: carry the most recent heading into a page whose own content
        // doesn't start with one, so every page still carries its section identity —
        // chunking happens per-page downstream, so this can't be recovered later.
        var pages        = new List<PdfPageRecord>();
        string? carryHeading = null;

        static bool IsHeading(string s) => s.StartsWith("# ") || s.StartsWith("## ");

        for (var pageNum = 1; pageNum <= pageCount; pageNum++)
        {
            var segments = pageSegments.TryGetValue(pageNum, out var s) ? s : [];

            if (segments.Count > 0 && !IsHeading(segments[0]) && carryHeading != null)
                segments = [carryHeading, .. segments];

            var lastHeadingOnPage = segments.LastOrDefault(IsHeading);
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
    // flat " | " join of every cell in reading order. Row/column spans are ignored:
    // a merged cell fills only its anchor slot, which is acceptable for this use.
    private static string RenderMarkdownTable(DocumentTable table)
    {
        if (table.RowCount == 0 || table.ColumnCount == 0) return "";

        var grid = new string?[table.RowCount, table.ColumnCount];
        foreach (var cell in table.Cells)
            if (cell.RowIndex < table.RowCount && cell.ColumnIndex < table.ColumnCount)
                grid[cell.RowIndex, cell.ColumnIndex] = cell.Content?.Trim();

        // DI marks header cells with Kind == ColumnHeader; header rows are assumed
        // contiguous from the top (covers 0, 1, or multi-row headers). When no cell
        // carries that kind at all, fall back to treating row 0 as the header so the
        // output stays a valid markdown table.
        var headerRowCount = 0;
        while (headerRowCount < table.RowCount
               && table.Cells.Any(c => c.RowIndex == headerRowCount && c.Kind == DocumentTableCellKind.ColumnHeader))
            headerRowCount++;
        if (headerRowCount == 0) headerRowCount = 1;

        var sb = new StringBuilder();
        for (var r = 0; r < table.RowCount; r++)
        {
            sb.Append('|');
            for (var c = 0; c < table.ColumnCount; c++)
                sb.Append(' ').Append(grid[r, c] ?? "").Append(" |");
            sb.Append('\n');

            if (r == headerRowCount - 1)
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
