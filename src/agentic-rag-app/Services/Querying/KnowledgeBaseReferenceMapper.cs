using Azure.Search.Documents.KnowledgeBases.Models;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// Converts knowledge-base references into RetrievedChunk records the rest of the
// querying pipeline works with. SourceData values are BinaryData (raw JSON), so
// they're deserialized rather than ToString()'d, which would leave string fields
// JSON-quoted/escaped.
public static class KnowledgeBaseReferenceMapper
{
    public static IReadOnlyList<RetrievedChunk> Map(IEnumerable<KnowledgeBaseReference> references)
    {
        var chunks = new List<RetrievedChunk>();
        foreach (var r in references)
        {
            if (r.SourceData is null || !r.SourceData.TryGetValue("content", out var contentRaw))
                continue;
            var content = AsText(contentRaw);
            if (string.IsNullOrWhiteSpace(content))
                continue;

            r.SourceData.TryGetValue("id", out var idRaw);
            r.SourceData.TryGetValue("document_id", out var docIdRaw);
            r.SourceData.TryGetValue("title", out var titleRaw);
            r.SourceData.TryGetValue("page_number", out var pageRaw);
            r.SourceData.TryGetValue("chunk_index", out var chunkIndexRaw);

            chunks.Add(new RetrievedChunk(
                Id:         AsText(idRaw) ?? "",
                DocumentId: AsText(docIdRaw) ?? "",
                Page:       AsInt(pageRaw),
                ChunkIndex: AsInt(chunkIndexRaw),
                Title:      AsText(titleRaw),
                Content:    content));
        }
        return chunks;
    }

    private static string? AsText(object? value) => value switch
    {
        null => null,
        BinaryData bd => bd.ToObjectFromJson<string>(),
        _ => value.ToString(),
    };

    private static int AsInt(object? value) => value switch
    {
        null => 0,
        BinaryData bd => bd.ToObjectFromJson<int>(),
        _ => Convert.ToInt32(value),
    };
}
