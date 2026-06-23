using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public interface IChunkingStrategy
{
    string Name { get; }
    IReadOnlyList<TextChunk> Chunk(string content);
}
