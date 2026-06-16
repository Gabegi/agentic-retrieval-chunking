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

    public int  TokenEstimate => Content.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    public bool IsEmpty       => string.IsNullOrWhiteSpace(Content);
    public bool IsOversized   => TokenEstimate > 1024;
    public bool IsUndersized  => TokenEstimate < 20;
}
