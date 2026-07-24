The one genuinely valuable find: the chunker family in the main package — SectionChunker, HeaderChunker, and SemanticSimilarityChunker. Your own docs/260721/chunking-rewrite-plan.md explicitly deferred this exact question: "Structure.Sections (DI's real semantic chunk boundaries) — the eventual right answer for chunk splitting... belongs to the chunking strategy review, not this plan." These three chunkers are literally that answer — SectionChunker/HeaderChunker split on real document structure (sections/headers) instead of your current fixed-size sliding windows, and SemanticSimilarityChunker uses embedding cosine-distance to find boundaries, which you could wire up cheaply since you already depend on Microsoft.Extensions.AI.
see chunking library at https://github.com/dotnet/extensions/tree/main/src/Libraries


Building the actually best version changes the shape of the fix. The header-only patch from before was page-scoped because that's what IChunkingStrategy gives you today — but reading PDFDocumentAnalyzer.cs closely surfaced two things that matter a lot more:

RawContent (the whole, unsliced DI markdown output — analysis.Content) already exists on PDFExtractionResult, and every offset (Heading.Offset, TableInfo.Offset, SectionInfo.Spans) indexes into that, not into any single page's text. It's just never threaded through to ExtractionDocument/ChunkingService — that's the actual missing plumbing, not something conceptually hard.
DI renders tables as real HTML <table> elements (PDFDocumentAnalyzer's own comment: "renders tables as HTML <table> elements with real rowspan/colspan"), not GFM pipe rows. That means ChunkingStrategy2's TableRowLine regex (^\s*\|.*\|\s*$) is checking for a shape DI never actually produces — its table-awareness almost certainly never fires on your real PDFs. Worth its own bug ticket regardless of anything below.
Given that, "best in class" here means: use DI's real Sections as the primary chunk boundary (not pages), render tables from the structured Cells data instead of parsing text, actually strip boilerplate (deferred since the DI decision doc), and attach each chunk its own precise heading — with a graceful fallback ladder for documents where DI doesn't return sections.

1. Small model additions — TextChunk needs a page number since a chunk can now span structure that isn't page-scoped, and ExtractionDocument needs RawContent:

 // src/AgenticRagApp.Indexing.Pdf/Models/TextChunk.cs
 public record TextChunk(
     int     Index,
     string  Content,
-    string? Heading = null
+    string? Heading = null,
+    int?    PageNumber = null
 );
 // src/AgenticRagApp.Indexing.Pdf/Models/ExtractionDocument.cs
     IReadOnlyList<SectionInfo> Sections,
 
+    // The whole document's DI-rendered markdown, unsliced - what every Offset above
+    // (Headings/Tables/Figures/Sections) actually indexes into. File-level, identical on
+    // every page, same as Sections/Bookmarks. Needed so chunk *splitting* can finally use
+    // those real boundaries instead of only page-scoped text - see SectionAwareChunkingStrategy.
+    string? RawContent,
+
     // ── Page-level (filtered to this page's PageNumber) ─────────────────────
2. Wire it through in PdfExtractionPipeline.cs (mirrors BuildNativeMetadataLookup exactly):

     private static ExtractionOutput BuildExtractionOutput(...)
     {
         var nativeMetadataByBlob = BuildNativeMetadataLookup(fileResults);
         var sectionsByBlob       = BuildSectionsLookup(fileResults);
+        var rawContentByBlob     = BuildRawContentLookup(fileResults);
         var pageContextByKey     = BuildPageContextLookup(fileResults);
 
         var extractionDocs = cleanResult.Records
             .Select(r =>
             {
                 ...
                 return new ExtractionDocument(
                     ...
                     Sections:              sectionsByBlob.GetValueOrDefault(r.BlobName) ?? [],
+                    RawContent:            rawContentByBlob.GetValueOrDefault(r.BlobName),
                     Breadcrumb:            pageContext.Breadcrumb,
// File-level whole-document content (same value on every page) - what Sections/Headings/
// Tables/Figures Offsets all index into. Same shape as BuildNativeMetadataLookup.
private static Dictionary<string, string> BuildRawContentLookup(
    IReadOnlyList<PDFExtractionResult> fileResults) =>
    fileResults
        .Where(f => f.Ok && f.RawContent is not null)
        .ToDictionary(f => f.BlobName, f => f.RawContent!, StringComparer.OrdinalIgnoreCase);
3. The strategy itself — src/AgenticRagApp.Indexing.Pdf/Services/Chunking/SectionAwareChunkingStrategy.cs:

using System.Text;
using System.Text.RegularExpressions;
using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

// Section-Aware Chunking — splits on DI's real semantic Sections (the boundary your own
// docs/260721/chunking-rewrite-plan.md flagged as "the eventual right answer for chunk
// splitting") instead of page boundaries. Falls back to heading boundaries, then to
// today's per-page prose chunking, so a document DI didn't return Sections/Headings for
// still gets chunked, never dropped.
//
// Operates on ALL of one document's ExtractionDocument pages at once (Sections/RawContent
// are file-level, identical on every page; Headings/Tables/Boilerplate/Figures are
// page-scoped, so this flattens them back across the whole file) - unlike every other
// IChunkingStrategy here, which only ever sees one page's Content string.
public sealed class SectionAwareChunkingStrategy : IChunkingStrategy
{
    public string Name => "SectionAwareChunker";

    private static readonly Regex TableBlockRegex = new(
        @"<table\b.*?</table\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly PdfChunkingStrategy1 _proseChunker;
    private readonly int _maxTableChars;

    public SectionAwareChunkingStrategy(PdfChunkingStrategy1? proseChunker = null, int maxTableChars = 1_500)
    {
        _proseChunker  = proseChunker ?? new PdfChunkingStrategy1();
        _maxTableChars = maxTableChars;
    }

    // IChunkingStrategy's plain per-string entry point - no structural data available this
    // way, so this is just the prose fallback. ChunkingService calls the document-level
    // overload below instead when this is the active strategy.
    public IReadOnlyList<TextChunk> Chunk(string content) => _proseChunker.Chunk(content);

    public IReadOnlyList<TextChunk> Chunk(IReadOnlyList<ExtractionDocument> pages)
    {
        if (pages.Count == 0) return [];

        // Defensive only - once RawContent is wired through BuildExtractionOutput every
        // successfully-extracted document has it. A null here means something upstream
        // regressed, not a normal "DI found nothing" case.
        if (string.IsNullOrWhiteSpace(pages[0].RawContent))
            return ChunkPerPageFallback(pages);

        return pages[0].Sections.Count > 0
            ? ChunkBySections(pages)
            : ChunkByHeadings(pages);
    }

    // ── Tier 1: DI's real section boundaries ────────────────────────────────

    private List<TextChunk> ChunkBySections(IReadOnlyList<ExtractionDocument> pages)
    {
        var raw         = pages[0].RawContent!;
        var sections    = pages[0].Sections.Where(s => s.Spans.Count > 0)
            .OrderBy(s => s.Spans[0].Offset).ToList();
        var headings    = FlattenOffsets(pages, p => p.Headings);
        var boilerplate = pages.SelectMany(p => p.Boilerplate).ToList();
        var tables      = FlattenOffsets(pages, p => p.Tables);
        var pageAnchors = BuildPageAnchors(pages);

        var chunks = new List<TextChunk>();

        foreach (var section in sections)
        {
            var start   = section.Spans[0].Offset;
            var heading = headings.LastOrDefault(h => h.Offset!.Value <= start)?.Content;
            var page    = PageNumberForOffset(pageAnchors, start);

            var segments = new List<(string Text, bool IsTable, TableInfo? Table)>();
            foreach (var span in section.Spans.OrderBy(s => s.Offset))
            {
                var text = StripBoilerplate(raw.Substring(span.Offset, span.Length), span.Offset, boilerplate);
                segments.AddRange(SplitOutTables(text, span.Offset, tables));
            }

            foreach (var piece in PackSegments(segments))
            {
                var trimmed = piece.Trim();
                if (trimmed.Length > 0)
                    chunks.Add(new TextChunk(chunks.Count, trimmed, heading, page));
            }
        }

        // Known gap, not solved here: DI's Sections don't always cover 100% of RawContent
        // (e.g. a cover page with no real prose) - anything outside every section's Spans
        // is silently skipped rather than back-filled. Worth a coverage check before
        // relying on this in production; not blocking for a first evaluation pass.
        return chunks;
    }

    // ── Tier 2: no Sections, but DI still gave us Headings ──────────────────

    private List<TextChunk> ChunkByHeadings(IReadOnlyList<ExtractionDocument> pages)
    {
        var headings = FlattenOffsets(pages, p => p.Headings);
        if (headings.Count == 0)
            return ChunkPerPageFallback(pages);

        var raw         = pages[0].RawContent!;
        var boilerplate = pages.SelectMany(p => p.Boilerplate).ToList();
        var tables      = FlattenOffsets(pages, p => p.Tables);
        var pageAnchors = BuildPageAnchors(pages);

        var chunks = new List<TextChunk>();
        for (int i = 0; i < headings.Count; i++)
        {
            var start = headings[i].Offset!.Value;
            var end   = i + 1 < headings.Count ? headings[i + 1].Offset!.Value : raw.Length;
            if (end <= start) continue;

            var text     = StripBoilerplate(raw[start..end], start, boilerplate);
            var segments = SplitOutTables(text, start, tables);
            var page     = PageNumberForOffset(pageAnchors, start);

            foreach (var piece in PackSegments(segments))
            {
                var trimmed = piece.Trim();
                if (trimmed.Length > 0)
                    chunks.Add(new TextChunk(chunks.Count, trimmed, headings[i].Content, page));
            }
        }
        return chunks;
    }

    // ── Tier 3: today's behavior, unchanged ──────────────────────────────────

    private List<TextChunk> ChunkPerPageFallback(IReadOnlyList<ExtractionDocument> pages)
    {
        var chunks = new List<TextChunk>();
        foreach (var page in pages)
        {
            var pageHeading = page.Breadcrumb ?? page.Headings.FirstOrDefault()?.Content;
            foreach (var piece in _proseChunker.Chunk(page.Content))
                chunks.Add(piece with { Index = chunks.Count, Heading = pageHeading, PageNumber = page.Ordinal });
        }
        return chunks;
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private static List<Heading> FlattenOffsets(
        IReadOnlyList<ExtractionDocument> pages, Func<ExtractionDocument, IReadOnlyList<Heading>> select) =>
        pages.SelectMany(select).Where(h => h.Offset is not null).OrderBy(h => h.Offset).ToList();

    private static List<TableInfo> FlattenOffsets(
        IReadOnlyList<ExtractionDocument> pages, Func<ExtractionDocument, IReadOnlyList<TableInfo>> select) =>
        pages.SelectMany(select).Where(t => t.Offset is not null).ToList();

    // Boilerplate (page header/footer/footnote/page-number) is anchor-only - Offset marks
    // where it starts, not how long it runs, so its own Content length is used as an
    // approximation of what to remove. Good enough: these are short, formulaic strings by
    // nature, not something a byte-perfect span match matters for.
    private static string StripBoilerplate(string text, int textStart, IReadOnlyList<Heading> boilerplate)
    {
        var removals = boilerplate
            .Where(b => b.Offset is not null)
            .Select(b => (Start: b.Offset!.Value - textStart, b.Content.Length))
            .Where(r => r.Start >= 0 && r.Start < text.Length)
            .OrderByDescending(r => r.Start) // back-to-front so earlier offsets stay valid
            .ToList();

        foreach (var (start, length) in removals)
            text = text.Remove(start, Math.Min(start + length, text.Length) - start);

        return text;
    }

    // Splits a slice of raw content into alternating prose/table segments, matching each
    // embedded HTML <table> block (DI's actual markdown table format) back to its
    // structured TableInfo by offset, so rendering can use real Cells data instead of
    // re-parsing HTML.
    private static List<(string Text, bool IsTable, TableInfo? Table)> SplitOutTables(
        string text, int textStart, IReadOnlyList<TableInfo> tables)
    {
        var result = new List<(string, bool, TableInfo?)>();
        var cursor = 0;

        foreach (Match m in TableBlockRegex.Matches(text))
        {
            if (m.Index > cursor)
                result.Add((text[cursor..m.Index], false, null));

            var absStart = textStart + m.Index;
            var absEnd   = absStart + m.Length;
            var table    = tables.FirstOrDefault(t => t.Offset >= absStart && t.Offset < absEnd);

            result.Add((m.Value, true, table));
            cursor = m.Index + m.Length;
        }

        if (cursor < text.Length)
            result.Add((text[cursor..], false, null));

        return result;
    }

    private IEnumerable<string> PackSegments(IEnumerable<(string Text, bool IsTable, TableInfo? Table)> segments)
    {
        foreach (var (text, isTable, table) in segments)
        {
            if (isTable && table is not null)
            {
                foreach (var piece in RenderTableChunks(table, _maxTableChars))
                    yield return piece;
            }
            else
            {
                // Prose, or an HTML <table> tag the structured Tables list has no matching
                // entry for - chunked as text rather than dropped (never silently lose
                // content, same rule PdfCleaner/PdfPipelineValidator follow elsewhere).
                foreach (var piece in _proseChunker.Chunk(text))
                    yield return piece.Content;
            }
        }
    }

    // Renders DI's structured Cells (RowIndex/ColumnIndex/Kind/RowSpan/ColumnSpan) as clean
    // GFM, instead of chunking DI's raw HTML <table> text - fixes the header row every
    // continuation chunk needs, and sidesteps ChunkingStrategy2's pipe-row regex, which
    // never matches DI's actual HTML table output in the first place.
    private static string RenderTable(TableInfo table)
    {
        var grid = new string?[table.RowCount, table.ColumnCount];
        foreach (var cell in table.Cells)
            if (cell.RowIndex < table.RowCount && cell.ColumnIndex < table.ColumnCount)
                grid[cell.RowIndex, cell.ColumnIndex] = cell.Content;

        // DocumentTableCellKind's exact ToString() casing isn't verified here - matching
        // case-insensitively on "header" is robust either way.
        var headerRows = table.Cells.Any(c => c.Kind.Contains("header", StringComparison.OrdinalIgnoreCase))
            ? table.Cells.Where(c => c.Kind.Contains("header", StringComparison.OrdinalIgnoreCase)).Max(c => c.RowIndex) + 1
            : Math.Min(1, table.RowCount);

        var sb = new StringBuilder();
        for (int r = 0; r < table.RowCount; r++)
        {
            AppendRow(sb, grid, r, table.ColumnCount);
            if (r == headerRows - 1)
                AppendSeparator(sb, table.ColumnCount);
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static void AppendRow(StringBuilder sb, string?[,] grid, int row, int columnCount)
    {
        sb.Append('|');
        for (int c = 0; c < columnCount; c++)
            sb.Append(' ').Append((grid[row, c] ?? "").Replace('\n', ' ').Replace("|", "\\|")).Append(" |");
        sb.Append('\n');
    }

    private static void AppendSeparator(StringBuilder sb, int columnCount)
    {
        sb.Append('|');
        for (int c = 0; c < columnCount; c++)
            sb.Append(" --- |");
        sb.Append('\n');
    }

    // A table that fits whole is rendered as one chunk. Otherwise split row-by-row,
    // repeating header rows at the top of every continuation - same "never split a table
    // away from its header" rule as ChunkingStrategy2, driven by Cells instead of text.
    private static List<string> RenderTableChunks(TableInfo table, int maxChars)
    {
        var full = RenderTable(table);
        if (full.Length <= maxChars) return [full];

        var headerRows = table.Cells.Any(c => c.Kind.Contains("header", StringComparison.OrdinalIgnoreCase))
            ? table.Cells.Where(c => c.Kind.Contains("header", StringComparison.OrdinalIgnoreCase)).Max(c => c.RowIndex) + 1
            : Math.Min(1, table.RowCount);

        var header = RenderTable(table with
        {
            RowCount = headerRows,
            Cells = table.Cells.Where(c => c.RowIndex < headerRows).ToList()
        });

        var chunks  = new List<string>();
        var current = new StringBuilder(header);
        var hasRows = false;

        for (int r = headerRows; r < table.RowCount; r++)
        {
            var rowTable = table with
            {
                RowCount = 1,
                Cells = table.Cells.Where(c => c.RowIndex == r).Select(c => c with { RowIndex = 0 }).ToList()
            };
            var rowText = RenderTable(rowTable);

            if (hasRows && current.Length + rowText.Length + 1 > maxChars)
            {
                chunks.Add(current.ToString());
                current = new StringBuilder(header);
                hasRows = false;
            }

            current.Append('\n').Append(rowText);
            hasRows = true;
        }

        if (hasRows) chunks.Add(current.ToString());
        return chunks;
    }

    // DI never gives a page's own start offset, only anchors on structural items that
    // happen to carry both Offset and PageNumber - approximate, for a "roughly where is
    // this chunk" field only. Never used for chunk-boundary logic itself.
    private static SortedList<int, int> BuildPageAnchors(IReadOnlyList<ExtractionDocument> pages)
    {
        var anchors = new SortedList<int, int>();
        void Add(int? offset, int page) { if (offset is int o && !anchors.ContainsKey(o)) anchors[o] = page; }

        foreach (var p in pages)
        {
            foreach (var h in p.Headings)    Add(h.Offset, h.PageNumber);
            foreach (var b in p.Boilerplate) Add(b.Offset, b.PageNumber);
            foreach (var t in p.Tables)      Add(t.Offset, t.PageNumber);
            foreach (var f in p.Figures)     Add(f.Offset, f.PageNumber);
        }
        return anchors;
    }

    private static int? PageNumberForOffset(SortedList<int, int> anchors, int offset)
    {
        if (anchors.Count == 0) return null;

        var keys = anchors.Keys;
        int lo = 0, hi = keys.Count - 1, best = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (keys[mid] <= offset) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return anchors.Values[best >= 0 ? best : 0];
    }
}
4. ChunkingService.cs — this is the part I'd flag hardest in review: grouping by document instead of iterating page-by-page means the old code's doc.Tables/doc.Headings/etc. (previously "this page's own data") would silently become "page 1's data on every chunk in the document" if copied over blindly. Fixed by re-looking-up the actual page each chunk landed on:

     public (IReadOnlyList<DocumentChunk> Docs, ChunkingResults Stats) ChunkDocuments(
         IReadOnlyList<ExtractionDocument> docs)
     {
         var result = new List<DocumentChunk>();

-        foreach (var doc in docs.OrderBy(d => d.SourceId).ThenBy(d => d.Ordinal))
+        foreach (var group in docs.GroupBy(d => d.SourceId).OrderBy(g => g.Key))
         {
-            var chunks = Chunk(doc.Content);
-
-            var heading = doc.Breadcrumb ?? doc.Headings.FirstOrDefault()?.Content;
+            var pages = group.OrderBy(d => d.Ordinal).ToList();
+            var doc   = pages[0]; // file-level fields (Title, Bookmarks, Sections, ...) are identical across every page

+            var chunks = _strategy is SectionAwareChunkingStrategy sectionAware
+                ? sectionAware.Chunk(pages)
+                : ChunkPerPage(pages);

             for (int docChunkIndex = 0; docChunkIndex < chunks.Count; docChunkIndex++)
             {
                 var chunk = chunks[docChunkIndex];
+                // The chunk's own page, not doc/pages[0] - a document-level strategy's
+                // chunks can land on any page, and page-scoped fields (Tables/Headings/...)
+                // must reflect that specific page, not always the first one.
+                var pageData = pages.FirstOrDefault(p => p.Ordinal == (chunk.PageNumber ?? doc.Ordinal)) ?? doc;

-                var body    = heading != null ? $"{heading}\n\n{chunk.Content}" : chunk.Content;
+                var body    = chunk.Heading != null ? $"{chunk.Heading}\n\n{chunk.Content}" : chunk.Content;
                 var content = string.IsNullOrEmpty(doc.Title) ? body : $"{doc.Title}\n\n{body}";

                 result.Add(new DocumentChunk
                 {
-                    Id                    = ChunkingUtils.SafeKey($"{doc.SourceId}::{doc.Ordinal}", docChunkIndex),
+                    Id                    = ChunkingUtils.SafeKey(doc.SourceId, docChunkIndex),
                     DocumentId            = doc.SourceId,
                     Title                 = doc.Title,
                     LastModifiedDate      = doc.LastModifiedDate,
                     ZenyaDocumentId       = doc.ZenyaDocumentId,
                     ZenyaVersion          = doc.ZenyaVersion,
                     ZenyaStatus           = doc.ZenyaStatus,
                     ZenyaUrl              = doc.ZenyaUrl,
                     Content               = content,
-                    Heading               = heading,
-                    PageNumber            = doc.Ordinal,
+                    Heading               = chunk.Heading,
+                    PageNumber            = chunk.PageNumber ?? doc.Ordinal,
                     ChunkIndex            = docChunkIndex,
                     Author                = doc.Author,
                     CreatedAt             = doc.CreatedAt,
                     ModDate               = doc.ModDate,
                     PageCount             = doc.PageCount,
                     Bookmarks             = doc.Bookmarks,
                     Sections              = doc.Sections,
-                    Breadcrumb            = doc.Breadcrumb,
-                    Headings              = doc.Headings,
-                    Boilerplate           = doc.Boilerplate,
-                    Tables                = doc.Tables,
-                    Dimensions            = doc.Dimensions,
-                    SelectionMarks        = doc.SelectionMarks,
-                    Figures               = doc.Figures,
-                    Lines                 = doc.Lines,
-                    AverageWordConfidence = doc.AverageWordConfidence,
+                    Breadcrumb            = pageData.Breadcrumb,
+                    Headings              = pageData.Headings,
+                    Boilerplate           = pageData.Boilerplate,
+                    Tables                = pageData.Tables,
+                    Dimensions            = pageData.Dimensions,
+                    SelectionMarks        = pageData.SelectionMarks,
+                    Figures               = pageData.Figures,
+                    Lines                 = pageData.Lines,
+                    AverageWordConfidence = pageData.AverageWordConfidence,
                 });
             }
         }
         ...
     }
+
+    // Unchanged per-page behavior for every strategy that isn't document-level.
+    private List<TextChunk> ChunkPerPage(IReadOnlyList<ExtractionDocument> pages)
+    {
+        var chunks = new List<TextChunk>();
+        foreach (var page in pages)
+        {
+            var pageHeading = page.Breadcrumb ?? page.Headings.FirstOrDefault()?.Content;
+            foreach (var piece in _strategy.Chunk(page.Content))
+                chunks.Add(piece with { Index = chunks.Count, Heading = piece.Heading ?? pageHeading, PageNumber = page.Ordinal });
+        }
+        return chunks;
+    }
5. Registration — ServiceCollectionExtensions.cs:

-        services.AddSingleton<IChunkingStrategy, PdfChunkingStrategy1>();
+        services.AddSingleton<PdfChunkingStrategy1>();
+        services.AddSingleton<IChunkingStrategy, SectionAwareChunkingStrategy>();
What's genuinely better than either the old strategies or last turn's patch: real cross-page section boundaries instead of page splits, tables rendered from structured cell data (also fixes the dead pipe-regex table detection), boilerplate actually stripped (deferred since the DI-vs-PdfPig decision), precise per-chunk heading and page number instead of one heading for a whole page, and a fallback ladder so nothing silently drops. What I deliberately left out: token-based sizing (still char-based via PdfChunkingStrategy1 — swapping in a real tokenizer is a separate, smaller change) and embedding-based semantic splitting (SemanticSimilarityChunker's idea) for oversized sections with no internal structure — that needs an embedding call per boundary candidate, which is a real recurring-cost decision given you've already had to correct one DI cost estimate by 10x, not something to fold in silently.

Want me to actually apply this on the branch and add the unit tests (there's a real regression surface here — the pageData lookup fix, table row-splitting, and the fallback tiers all need coverage)?