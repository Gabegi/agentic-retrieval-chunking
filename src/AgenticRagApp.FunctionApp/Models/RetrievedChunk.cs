namespace AgenticRagApp.Models;

public sealed record RetrievedChunk(
    string Id, string DocumentId, int Page, int ChunkIndex,
    string? Title, string? Summary, string Content,
    string? QuickCode = null, string? RelativePath = null)
{
    public string ToContextText()
    {
        var header = Title is null ? null : string.IsNullOrWhiteSpace(Summary) ? Title : $"{Title}\n{Summary}";
        return header is null ? Content : $"[{header}]\n{Content}";
    }
}
