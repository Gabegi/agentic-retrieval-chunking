using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public interface IChunkingService
{
    string Name { get; }
    IReadOnlyList<TextChunk> ChunkAsync(string content);
}
