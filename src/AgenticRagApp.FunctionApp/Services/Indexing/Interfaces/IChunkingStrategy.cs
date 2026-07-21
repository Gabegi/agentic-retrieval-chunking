using AgenticRagApp.Models;

namespace AgenticRagApp.Services;

public interface IChunkingStrategy
{
    string Name { get; }
    IReadOnlyList<TextChunk> Chunk(string content);
}
