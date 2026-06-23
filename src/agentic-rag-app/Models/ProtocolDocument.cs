using System.Text.Json.Serialization;

namespace ProtocolsIndexer.Models;

public class ProtocolDocument
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("source_file")]
    public string SourceFile { get; set; } = "";

    [JsonPropertyName("richtlijn_name")]
    public string? RichtlijnName { get; set; }

    [JsonPropertyName("publication_date")]
    public string? PublicationDate { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("heading")]
    public string? Heading { get; set; }

    [JsonPropertyName("page_number")]
    public int PageNumber { get; set; }

    [JsonPropertyName("chunk_index")]
    public int ChunkIndex { get; set; }

    [JsonPropertyName("content_vector")]
    public float[]? ContentVector { get; set; }

    [JsonIgnore] public int  TokenEstimate => Content.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    [JsonIgnore] public bool IsEmpty       => string.IsNullOrWhiteSpace(Content);
    [JsonIgnore] public bool IsOversized   => TokenEstimate > 1024;
    [JsonIgnore] public bool IsUndersized  => TokenEstimate < 20;

    // Sentence boundary proxies — a coherent chunk starts and ends at natural boundaries
    [JsonIgnore] public bool StartsClean => Content.Length > 0 && (char.IsUpper(Content[0]) || char.IsDigit(Content[0]));
    [JsonIgnore] public bool EndsClean   => Content.Length > 0 && ".!?:)\"'".Contains(Content[^1]);
    [JsonIgnore] public bool IsCoherent  => StartsClean && EndsClean;

    // Content already includes the section heading (prepended by extraction services),
    // so keyword and vector signals are aligned.
    [JsonIgnore] public string EmbeddingText => Content;
}
