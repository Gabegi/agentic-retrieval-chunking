using System.Text;
using Azure;
using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Models;
using System.Security.Cryptography;

namespace ProtocolsIndexer.Models
{
    // Small, capability-scoped return types for PDFStructureExtractor - one shape
    // per Get* method, so callers only pull in the fields relevant to what they asked for.

    // Offset is the join key for whatever builds ChunkMetadata later (position in the
    // single global result.Content string every span indexes into) - PageNumber is for
    // display/debugging only, since it can't order two headings on the same page.
    public sealed record Heading(string Content, string Role, int Offset, int PageNumber);

    public sealed record PageDimensions(int PageNumber, double? Width, double? Height, string Unit);

    public sealed record TableCellInfo(int RowIndex, int ColumnIndex, string Kind, string Content);

    public sealed record TableInfo(int RowCount, int ColumnCount, IReadOnlyList<TableCellInfo> Cells, int Offset, int PageNumber);

    public sealed record SelectionMarkInfo(int PageNumber, string State, int Offset);

    // Raw structural ingredients for one document - not chunk metadata itself. Chunk
    // boundaries don't exist at extraction time, so whatever builds ChunkMetadata later
    // (by joining on Heading/TableInfo/SelectionMarkInfo's Offset) does that joining -
    // this record only bundles what extraction already knows for free.
    public sealed record PdfStructureMetadata(
        DocMetadata NativeMetadata,
        IReadOnlyList<Heading> Headings,
        IReadOnlyList<Bookmark>? Bookmarks,
        IReadOnlyList<TableInfo> Tables,
        IReadOnlyList<PageDimensions> PageDimensions,
        IReadOnlyList<SelectionMarkInfo> SelectionMarks);

    // Result of the one paid DI call. Ok=false carries a typed ExtractionError instead of
    // an unhandled exception, so callers can branch on Reason (Throttled is worth a retry
    // upstream; DiServiceError probably isn't).
    public sealed record AnalyzeOutcome(bool Ok, AnalyzeResult? Result, ExtractionError? Error);

    // Full result of ExtractPdfStructureAsync - the one call DocumentIntelligenceExtractor
    // makes. Ok=false covers every failure point (preflight, the paid call) with a single
    // typed ExtractionError, same Ok/Error shape as AnalyzeOutcome one level up.
    public sealed record PdfStructureExtraction(
        bool Ok,
        IReadOnlyList<PdfPageRecord>? Pages,
        PdfIndexRecord? Index,
        PdfStructureMetadata? Metadata,
        decimal? EstimatedCostUsd,
        ExtractionError? Error);
}

namespace ProtocolsIndexer.Services
{
    // Everything the DocumentIntelligence backend needs from one PDF file except
    // preflight and PdfPig-native reads: the one paid analyze call (with retry on 429),
    // markdown page assembly, and every DI structural capability probe (headings/tables/
    // page dimensions/selection marks) — bundled for whatever builds ChunkMetadata later.
    // Preflight (PdfDocumentValidator.IsPDFValid) and the PdfPig-only reads
    // (PdfDocumentMetadataReader.ParseNativeMetadata/GetBookmarks) stay in
    // DocumentIntelligenceExtractor, since it owns the PdfDocument's lifetime and both
    // need that object open; this class only ever sees the raw bytes and the results of
    // those reads, passed in as parameters.
    public sealed class PDFStructureExtractor
    {
        // prebuilt-layout is billed at $10 / 1,000 pages. Verify against current
        // Azure pricing before trusting cost estimates derived from this constant.
        private const decimal CostPerPage = 0.01m;

        private static readonly TimeSpan[] BackoffDelays =
        {
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(13), TimeSpan.FromSeconds(34)
        };

        private readonly DocumentIntelligenceClient _diClient;
        private readonly ILogger _logger;

        public PDFStructureExtractor(DocumentIntelligenceClient diClient, ILogger logger)
        {
            _diClient = diClient;
            _logger = logger;
        }

        // The one method DocumentIntelligenceExtractor calls, once preflight has already
        // opened and validated the PdfDocument (PdfDocumentValidator.IsPDFValid) and read
        // nativeMetadata/bookmarks off it - both need the PdfDocument, which is disposed
        // by the time this runs, so the caller reads them first and passes the results in
        // rather than this method re-opening the file. From here: submit to Document
        // Intelligence, then assemble markdown pages and every structural probe from the
        // same AnalyzeResult. Ok=false covers the paid call's failure with a typed
        // ExtractionError.
        public async Task<PdfStructureExtraction> ExtractPdfStructureAsync(
            byte[] pdfBytes, string blobName, DocMetadata nativeMetadata, IReadOnlyList<Bookmark>? bookmarks, CancellationToken ct = default)
        {
            var analyzeOutcome = await AnalyzeDocumentAsync(pdfBytes, blobName, ct);
            if (!analyzeOutcome.Ok)
                return new PdfStructureExtraction(false, null, null, null, null, analyzeOutcome.Error);

            var analysis  = analyzeOutcome.Result!;
            var pageCount = analysis.Pages?.Count ?? 0;

            // Preserve newlines so multiline regexes in PdfMetadataExtraction anchor correctly.
            var firstPagesText = string.Join("\n",
                analysis.Paragraphs?
                    .Where(p => (p.BoundingRegions is { Count: > 0 } br0 ? br0[0].PageNumber : 0) <= 2)
                    .Select(p => p.Content) ?? []);

            var index = PdfMetadataExtraction.Parse(blobName, firstPagesText);
            var pages = BuildMarkdownPages(blobName, analysis, pageCount, bookmarks);

            var metadata = new PdfStructureMetadata(
                nativeMetadata,
                GetHeadings(analysis),
                bookmarks,
                GetTables(analysis),
                GetPageDimensions(analysis),
                GetSelectionMarks(analysis));

            return new PdfStructureExtraction(true, pages, index, metadata, pageCount * CostPerPage, null);
        }

        // The one paid call. Retries on 429 using the backoff pattern MS's own docs
        // recommend (2-5-13-34s). NB: WaitUntil.Completed polls internally, and a known
        // SDK issue (Azure/azure-sdk-for-net#50904) means that internal poll can still 429
        // independent of any DocumentIntelligenceClientOptions.Retry configured where the
        // client was constructed - this loop is why that gap doesn't just surface as an
        // unhandled exception. Any other failure returns Ok=false with a typed reason
        // instead of throwing.
        private async Task<AnalyzeOutcome> AnalyzeDocumentAsync(byte[] pdfBytes, string blobName, CancellationToken ct)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    _logger.LogInformation("Submitting '{Blob}' to Document Intelligence (attempt {Attempt}).", blobName, attempt + 1);

                    Operation<AnalyzeResult> operation = await _diClient.AnalyzeDocumentAsync(
                        WaitUntil.Completed, "prebuilt-layout", BinaryData.FromBytes(pdfBytes), cancellationToken: ct);

                    return new AnalyzeOutcome(true, operation.Value, null);
                }
                catch (RequestFailedException ex) when (ex.Status == 429 && attempt < BackoffDelays.Length)
                {
                    var wait = BackoffDelays[attempt];
                    _logger.LogWarning("DI throttled '{Blob}' (attempt {Attempt}); backing off {Wait}.", blobName, attempt + 1, wait);
                    await Task.Delay(wait, ct);
                }
                catch (RequestFailedException ex)
                {
                    _logger.LogWarning(ex, "Document Intelligence failed to analyze '{Blob}'.", blobName);
                    return new AnalyzeOutcome(false, null, new ExtractionError
                    {
                        DocumentId = blobName,
                        Message = $"Document Intelligence request failed ({ex.Status}): {ex.Message}",
                        Reason = ex.Status == 429 ? PdfOpenFailureReason.Throttled : PdfOpenFailureReason.DiServiceError,
                    });
                }
            }
        }

        // Stable content-hash for dedup/caching keys upstream - same bytes always produce
        // the same key regardless of blob name, so re-uploads of an unchanged file can be
        // recognized before paying for another analyze call.
        public static string ComputeContentHash(byte[] pdfBytes) =>
            Convert.ToHexString(SHA256.HashData(pdfBytes));

        // Assembles one PdfPageRecord per PDF page with markdown-flavored content
        // ("## " headings, real column-aware pipe tables) - the DocumentIntelligence
        // backend's actual output shape, as opposed to the Get* probes below, which exist
        // for the DI-vs-PdfPig capability comparison and deliberately drop the
        // page-number/span-offset info this method needs to place content in order.
        //
        // bookmarks, when available, prepend a section breadcrumb ("_Section: Chapter 3 >
        // 3.2 Dosage_") to every page under that outline entry - a stronger signal of a
        // page's place in the document than DI's own per-paragraph heading roles, which
        // this breadcrumb supplements rather than replaces (the "## " headings below still
        // come from DI's Role classification). null/empty bookmarks (no outline in this
        // PDF, or PdfPig failed to read one) just means no breadcrumb - never a hard
        // requirement for extraction to succeed.
        public List<PdfPageRecord> BuildMarkdownPages(
            string blobName, AnalyzeResult analysis, int pageCount, IReadOnlyList<Bookmark>? bookmarks = null)
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
                    _logger.LogWarning("DocumentIntelligence: table with no bounding region dropped in {BlobName}", blobName);
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
                    _logger.LogWarning("DocumentIntelligence: paragraph with no bounding region dropped in {BlobName}", blobName);
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

            var breadcrumbByPage     = BuildSectionBreadcrumbs(bookmarks, pageCount);
            var selectionBlockByPage = BuildSelectionMarkBlocks(analysis);

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

                if (breadcrumbByPage.TryGetValue(pageNum, out var breadcrumb))
                    segments = [breadcrumb, .. segments];

                if (selectionBlockByPage.TryGetValue(pageNum, out var selectionBlock))
                    segments = [.. segments, selectionBlock];

                pages.Add(new PdfPageRecord
                {
                    BlobName    = blobName,
                    PageIndex   = pageNum,
                    PageContent = string.Join("\n\n", segments),
                });
            }

            return pages;
        }

        // Reduces the flat, pre-order bookmark list into one breadcrumb string per page -
        // the deepest outline entry active as of that page, joined with its ancestors
        // ("Chapter 3 > 3.2 Dosage"). Maintains a level-indexed stack while walking
        // bookmarks in page order: each entry truncates the stack to its own Level before
        // pushing, so a later top-level bookmark correctly drops any deeper section left
        // over from the previous chapter. Entries with no resolvable PageNumber (see
        // PdfDocumentMetadataReader.TryGetPageNumber) are skipped - they can't anchor a
        // page range and would otherwise corrupt the stack with an untethered entry.
        private static Dictionary<int, string> BuildSectionBreadcrumbs(IReadOnlyList<Bookmark>? bookmarks, int pageCount)
        {
            var result = new Dictionary<int, string>();
            if (bookmarks is not { Count: > 0 }) return result;

            var ordered = bookmarks
                .Where(b => b.PageNumber is > 0)
                .OrderBy(b => b.PageNumber)
                .ToList();
            if (ordered.Count == 0) return result;

            var stack = new List<string>();
            var breakpoints = new List<(int PageNumber, string Path)>();

            foreach (var bm in ordered)
            {
                if (stack.Count > bm.Level) stack.RemoveRange(bm.Level, stack.Count - bm.Level);
                while (stack.Count < bm.Level) stack.Add("");
                stack.Add(bm.Title);

                breakpoints.Add((bm.PageNumber!.Value, string.Join(" > ", stack)));
            }

            var breakpointIndex = 0;
            string? current = null;
            for (var pageNum = 1; pageNum <= pageCount; pageNum++)
            {
                while (breakpointIndex < breakpoints.Count && breakpoints[breakpointIndex].PageNumber <= pageNum)
                    current = breakpoints[breakpointIndex++].Path;

                if (current != null)
                    result[pageNum] = $"_Section: {current}_";
            }

            return result;
        }

        // Checkboxes/radio buttons: DocumentSelectionMark has no paragraph of its own - DI
        // treats it as a non-text glyph, so it never appears in analysis.Paragraphs and
        // (unlike tables) nothing upstream of this method reads analysis.Pages[].SelectionMarks
        // at all, meaning "which option was checked" is silently dropped from every page
        // today. DI gives no direct link from a mark to its label, so the nearest paragraph
        // on the same page (by absolute span-offset distance) is used as a best-effort
        // label. Rendered as its own "Selected options" block appended after a page's
        // regular content - not spliced into the paragraph flow - so a wrong nearest-
        // neighbor guess can't silently corrupt unrelated prose.
        private static Dictionary<int, string> BuildSelectionMarkBlocks(AnalyzeResult analysis)
        {
            var result = new Dictionary<int, string>();

            var paragraphsByPage = (analysis.Paragraphs ?? [])
                .Select(p => (
                    Offset: p.Spans is { Count: > 0 } ps ? (int?)ps[0].Offset : null,
                    PageNumber: p.BoundingRegions is { Count: > 0 } br ? br[0].PageNumber : 0,
                    p.Content))
                .Where(p => p.Offset is not null && p.PageNumber > 0)
                .GroupBy(p => p.PageNumber)
                .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Offset).ToList());

            foreach (var page in analysis.Pages ?? [])
            {
                if (page.SelectionMarks is not { Count: > 0 } marks) continue;

                var lines = new List<string>();
                foreach (var mark in marks.OrderBy(m => m.Span.Offset))
                {
                    var label = "(unlabeled)";
                    if (paragraphsByPage.TryGetValue(page.PageNumber, out var candidates) && candidates.Count > 0)
                    {
                        var nearestText = candidates
                            .OrderBy(c => Math.Abs(c.Offset!.Value - mark.Span.Offset))
                            .First().Content?.Trim();

                        if (!string.IsNullOrEmpty(nearestText))
                            label = nearestText.Length > 100 ? nearestText[..100] + "…" : nearestText;
                    }

                    var box = mark.State == DocumentSelectionMarkState.Selected ? "[x]" : "[ ]";
                    lines.Add($"- {box} {label}");
                }

                result[page.PageNumber] = "**Selected options:**\n" + string.Join("\n", lines);
            }

            return result;
        }

        // Headings/sections: paragraphs DI classified with a structural role (title,
        // sectionHeading, pageHeader, footnote, ...) rather than plain body text.
        // Offset/PageNumber come from Spans/BoundingRegions - DocumentParagraph has no
        // PageNumber property of its own (same pattern BuildMarkdownPages already uses).
        public IReadOnlyList<Heading> GetHeadings(AnalyzeResult result) =>
            result.Paragraphs
                .Where(p => p.Role is not null)
                .Select(p => new Heading(
                    p.Content,
                    p.Role.ToString()!,
                    p.Spans is { Count: > 0 } ps ? ps[0].Offset : 0,
                    p.BoundingRegions is { Count: > 0 } br ? br[0].PageNumber : 0))
                .ToList();

        // Page geometry as DI measured it (not the PDF's declared MediaBox).
        public IReadOnlyList<PageDimensions> GetPageDimensions(AnalyzeResult result) =>
            result.Pages
                .Select(p => new PageDimensions(p.PageNumber, p.Width, p.Height, p.Unit.ToString() ?? ""))
                .ToList();

        // Tables with row/column position and cell kind (e.g. columnHeader vs content) per
        // cell. Offset/PageNumber follow the same Spans/BoundingRegions pattern as
        // GetHeadings - DocumentTable has no PageNumber property of its own either.
        public IReadOnlyList<TableInfo> GetTables(AnalyzeResult result) =>
            result.Tables
                .Select(t => new TableInfo(
                    t.RowCount,
                    t.ColumnCount,
                    t.Cells.Select(c => new TableCellInfo(c.RowIndex, c.ColumnIndex, c.Kind.ToString() ?? "", c.Content)).ToList(),
                    t.Spans is { Count: > 0 } ts ? ts[0].Offset : 0,
                    t.BoundingRegions is { Count: > 0 } br ? br[0].PageNumber : 0))
                .ToList();

        // Checkboxes/radio buttons with selected/unselected state, per page. Offset comes
        // from Span (singular - one mark, one position, unlike paragraphs/tables' Spans).
        public IReadOnlyList<SelectionMarkInfo> GetSelectionMarks(AnalyzeResult result) =>
            result.Pages
                .SelectMany(p => p.SelectionMarks.Select(sm => new SelectionMarkInfo(p.PageNumber, sm.State.ToString(), sm.Span.Offset)))
                .ToList();

        // Handwritten spans at >0.8 confidence (same threshold as the original quickstart
        // sample). Styles point at spans into result.Content - they don't carry the text
        // directly, so this substrings it out.
        public IReadOnlyList<string> GetHandwrittenContent(AnalyzeResult result) =>
            result.Styles
                .Where(s => s.IsHandwritten == true && s.Confidence > 0.8)
                .SelectMany(s => s.Spans)
                .Select(span => result.Content.Substring(span.Offset, span.Length))
                .ToList();

        // Column-aware markdown table rendering using Document Intelligence's row/column
        // indices — a real pipe table with a header row, unlike the comparison spike's
        // flat " | " join of every cell in reading order. Row/column spans are ignored:
        // a merged cell fills only its anchor slot, which is acceptable for this use.
        // Caption/footnotes are rendered around the table rather than dropped — a table's
        // caption ("Table 3: Dosage schedule") is often the strongest retrieval signal for
        // that table's content, and their spans fall inside the table's own Spans range, so
        // they're already excluded from the paragraph loop above (no duplicate emission).
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

            var caption = table.Caption?.Content?.Trim();
            if (!string.IsNullOrEmpty(caption))
                sb.Append("**").Append(caption).Append("**\n\n");

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

            foreach (var footnote in table.Footnotes ?? [])
            {
                var text = footnote.Content?.Trim();
                if (!string.IsNullOrEmpty(text))
                    sb.Append("\n_").Append(text).Append("_\n");
            }

            return sb.ToString().TrimEnd();
        }
    }
}
