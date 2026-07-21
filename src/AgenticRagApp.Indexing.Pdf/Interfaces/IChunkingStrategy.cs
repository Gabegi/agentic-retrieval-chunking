using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

public interface IChunkingStrategy
{
    string Name { get; }
    IReadOnlyList<TextChunk> Chunk(string content);
}
