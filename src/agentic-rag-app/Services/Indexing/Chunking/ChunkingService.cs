using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public class ChunkingService : IChunkingService
{
    // Strategy is injected — swap implementations in program.cs.
    // All available strategies are in Services/Indexing/Chunking/.
    private readonly IChunkingStrategy _strategy;

    public string Name => _strategy.Name;

    public ChunkingService(IChunkingStrategy strategy) => _strategy = strategy;

    public IReadOnlyList<TextChunk> Chunk(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        return _strategy.Chunk(content);
    }
}
