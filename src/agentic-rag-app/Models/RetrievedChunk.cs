namespace ProtocolsIndexer.Models;

public sealed record RetrievedChunk(string Id, string DocumentId, int Page, int ChunkIndex, string? Title, string Content)
{
    public string ToContextText() => Title is null ? Content : $"[{Title}]\n{Content}";
}
