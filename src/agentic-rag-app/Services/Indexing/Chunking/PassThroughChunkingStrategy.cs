using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// Placeholder strategy: returns the entire content as one chunk.
// Replace with MarkdownHeadingChunker / SlidingWindowChunker once implemented.
public class PassThroughChunkingStrategy : IChunkingStrategy
{
    public string Name => "PassThrough";

    public IReadOnlyList<TextChunk> Chunk(string content) =>
        [new TextChunk(0, content)];
}
