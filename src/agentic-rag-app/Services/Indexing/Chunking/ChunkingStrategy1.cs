using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// Sentence-aware sliding-window: splits at sentence boundaries up to maxChars,
// with overlapChars of back-overlap so facts near a split appear in both chunks.
// Mirrors the ChunkingUtils.SplitContent logic used by both PDF extraction services.
public class ChunkingStrategy1 : IChunkingStrategy
{
    private readonly int _maxChars;
    private readonly int _overlapChars;

    public string Name => "SentenceAwareSlidingWindow";

    public ChunkingStrategy1(int maxChars = 1_500, int overlapChars = 150)
    {
        _maxChars     = maxChars;
        _overlapChars = overlapChars;
    }

    public IReadOnlyList<TextChunk> Chunk(string content)
    {
        var chunks = new List<TextChunk>();
        int index  = 0;
        int start  = 0;

        while (start < content.Length)
        {
            if (start + _maxChars >= content.Length)
            {
                var tail = content[start..].Trim();
                if (!string.IsNullOrWhiteSpace(tail))
                    chunks.Add(new TextChunk(index++, tail));
                break;
            }

            int end   = start + _maxChars;
            int split = FindSentenceSplit(content, start, end)
                     ?? FindWordSplit(content, start, end)
                     ?? end;

            var part = content[start..split].Trim();
            if (!string.IsNullOrWhiteSpace(part))
                chunks.Add(new TextChunk(index++, part));

            int nextStart = split;
            if (_overlapChars > 0 && split - _overlapChars > start)
            {
                for (int i = split - 1; i > split - _overlapChars; i--)
                {
                    if (i > 0 && content[i - 1] is '.' or '!' or '?' && content[i] == ' ')
                    { nextStart = i + 1; break; }
                }
                if (nextStart == split)
                    nextStart = Math.Max(start + 1, split - _overlapChars);
            }

            start = nextStart;
        }

        return chunks;
    }

    private static int? FindSentenceSplit(string text, int start, int end)
    {
        for (int i = end; i > start + (end - start) / 2; i--)
        {
            if (text[i] is '.' or '!' or '?' && i + 1 < text.Length && text[i + 1] == ' ')
                return i + 1;
        }
        return null;
    }

    private static int? FindWordSplit(string text, int start, int end)
    {
        for (int i = end; i > start + (end - start) / 2; i--)
        {
            if (text[i] == ' ')
                return i + 1;
        }
        return null;
    }
}
