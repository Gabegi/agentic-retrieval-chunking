using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace AgenticRag.Models;

// One chunk of a PDF page, embedded and uploaded to Azure AI Search. Renamed from
// ProtocolDocument - that name was Zenya/CSV-era ("care protocols" specifically); this
// project only ever handles PDFs now (see docs/plan210726.md's "no generic" note).
public class DocumentChunk
{
    // ── Search-indexed fields (IndexService's schema) ───────────────────────

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("document_id")]
    public string DocumentId { get; set; } = "";

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("last_modified_date")]
    public DateTimeOffset? LastModifiedDate { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    // Real content now (Breadcrumb, or the first DI-detected heading) - previously always
    // null, since nothing ever set TextChunk.Heading. See ChunkingService.
    [JsonPropertyName("heading")]
    public string? Heading { get; set; }

    [JsonPropertyName("page_number")]
    public int PageNumber { get; set; }

    [JsonPropertyName("chunk_index")]
    public int ChunkIndex { get; set; }

    [JsonPropertyName("content_vector")]
    public float[]? ContentVector { get; set; }

    // ── Derived Search-indexed fields (Tier 2) ──────────────────────────────
    // Computed from the raw structural fields below, the same way TokenEstimate/IsEmpty
    // etc. further down are computed from Content - simple scalars/collections Search can
    // actually store, derived from the richer objects Search can't.

    [JsonPropertyName("table_count")]
    public int TableCount => Tables.Count;

    [JsonPropertyName("has_table")]
    public bool HasTable => Tables.Count > 0;

    [JsonPropertyName("page_quality")]
    public double? PageQuality => AverageWordConfidence;

    [JsonPropertyName("figure_captions")]
    public IReadOnlyList<string> FigureCaptions => Figures
        .Where(f => !string.IsNullOrWhiteSpace(f.Caption))
        .Select(f => f.Caption!)
        .ToList();

    // ── Everything else extraction produced ─────────────────────────────────
    // Not in the Search schema (no simple/collection field shape fits these - nested
    // objects like TableInfo's cells, or file-level data like Bookmarks/Sections) - but
    // NOT [JsonIgnore]'d. That attribute is type-level, not call-site-level: it would
    // strip these fields from every serialization of DocumentChunk, not just the Search
    // upload one - including the ChunkActivity -> EmbedAndUploadActivity blob hand-off
    // (chunks.json) and the Stage 2 archive, silently losing this data before it could
    // ever reach either. See SearchUploadChunk for the actual Search-only projection,
    // built right before the upload call instead.

    public string?         Author { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public int?            PageCount { get; set; }
    public IReadOnlyList<Bookmark>    Bookmarks { get; set; } = [];
    public IReadOnlyList<SectionInfo> Sections  { get; set; } = [];

    public string? Breadcrumb { get; set; }
    public IReadOnlyList<Heading>           Headings       { get; set; } = [];
    public IReadOnlyList<Heading>           Boilerplate    { get; set; } = [];
    public IReadOnlyList<TableInfo>         Tables         { get; set; } = [];
    public PageDimensions?                  Dimensions     { get; set; }
    public IReadOnlyList<SelectionMarkInfo> SelectionMarks { get; set; } = [];
    public IReadOnlyList<FigureInfo>        Figures        { get; set; } = [];
    public IReadOnlyList<LineInfo>          Lines          { get; set; } = [];
    public double?                          AverageWordConfidence { get; set; }

    [JsonIgnore] public int  TokenEstimate => Content.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    [JsonIgnore] public bool IsEmpty       => string.IsNullOrWhiteSpace(Content);
    [JsonIgnore] public bool IsOversized   => TokenEstimate > 1024;
    [JsonIgnore] public bool IsUndersized  => TokenEstimate < 20;

    // Sentence boundary proxies — a coherent chunk starts and ends at natural boundaries
    [JsonIgnore] public bool StartsClean => Content.Length > 0 && (char.IsUpper(Content[0]) || char.IsDigit(Content[0]));
    [JsonIgnore] public bool EndsClean   => Content.Length > 0 && ".!?:)\"'".Contains(Content[^1]);
    [JsonIgnore] public bool IsCoherent  => StartsClean && EndsClean;

    // Title and Breadcrumb/Heading are already prepended into Content by ChunkingService,
    // so this is just Content - kept as a named property (rather than every caller reading
    // Content directly) so "what gets embedded" and "what gets stored/searched" stay two
    // separately named concepts, even though they're identical today.
    [JsonIgnore] public string EmbeddingText => Content;

    // Hash of the exact text sent to the embedding API - a match means the embedding would
    // come back byte-identical, so EmbeddingService can skip the call and reuse the cached
    // vector instead. [JsonIgnore] for the same reason as the fields above - no matching
    // Search schema field.
    [JsonIgnore] public string ContentHash =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(EmbeddingText)));
}
