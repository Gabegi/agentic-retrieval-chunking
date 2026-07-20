using CsvIndexing.Models;

namespace CsvIndexing.Services;

public interface IChunkingStrategy
{
    string Name { get; }
    IReadOnlyList<TextChunk> Chunk(string content);
}
