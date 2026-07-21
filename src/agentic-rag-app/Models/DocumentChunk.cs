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

    // ── Everything else extraction produced ─────────────────────────────────
    // Not in the Search schema yet - adding a field requires the schema-migration
    // mechanism that doesn't exist yet (see docs/chunking-rewrite-plan.md's Tier 2).
    // [JsonIgnore] so upload never sends an unknown field to Search. Still carried through
    // in full so it's not silently dropped - available in the Stage 2 archive today, and
    // ready to promote to real schema fields once Tier 2 lands.

    [JsonIgnore] public string?         Author { get; set; }
    [JsonIgnore] public DateTimeOffset? CreatedAt { get; set; }
    [JsonIgnore] public int?            PageCount { get; set; }
    [JsonIgnore] public IReadOnlyList<Bookmark>    Bookmarks { get; set; } = [];
    [JsonIgnore] public IReadOnlyList<SectionInfo> Sections  { get; set; } = [];

    [JsonIgnore] public string? Breadcrumb { get; set; }
    [JsonIgnore] public IReadOnlyList<Heading>           Headings       { get; set; } = [];
    [JsonIgnore] public IReadOnlyList<Heading>           Boilerplate    { get; set; } = [];
    [JsonIgnore] public IReadOnlyList<TableInfo>         Tables         { get; set; } = [];
    [JsonIgnore] public PageDimensions?                  Dimensions     { get; set; }
    [JsonIgnore] public IReadOnlyList<SelectionMarkInfo> SelectionMarks { get; set; } = [];
    [JsonIgnore] public IReadOnlyList<FigureInfo>        Figures        { get; set; } = [];
    [JsonIgnore] public IReadOnlyList<LineInfo>          Lines          { get; set; } = [];
    [JsonIgnore] public double?                          AverageWordConfidence { get; set; }

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
