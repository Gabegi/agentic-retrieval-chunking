using System.Text.Json.Serialization;

namespace ProtocolsIndexer.Models;

public class ProtocolDocument
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("richtlijn_name")]
    public string? RichtlijnName { get; set; }

    [JsonPropertyName("source_file")]
    public string? SourceFile { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("content_vector")]
    public float[]? ContentVector { get; set; }
}
