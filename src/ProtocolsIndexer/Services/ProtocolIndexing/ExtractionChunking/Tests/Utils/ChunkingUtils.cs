using System.Text;

namespace ProtocolsIndexer.Utils;

public static class ChunkingUtils
{
    public static string SafeKey(string blobName, int index) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{blobName}::{index}"))
            .Replace('+', '-').Replace('/', '_');

    // Splits body text into chunks of at most maxChars, with overlapChars of overlap
    // so facts near a split boundary appear in both adjacent chunks.
    public static IEnumerable<string> SplitContent(string text, int maxChars = 1_500, int overlapChars = 150)
    {
        if (text.Length <= maxChars) { yield return text; yield break; }

        int start = 0;
        while (start < text.Length)
        {
            if (start + maxChars >= text.Length) { yield return text[start..].Trim(); yield break; }

            int end   = start + maxChars;
            int split = -1;

            for (int i = end; i > start + maxChars / 2; i--)
            {
                if (text[i] is '.' or '!' or '?' && i + 1 < text.Length && text[i + 1] == ' ')
                { split = i + 1; break; }
            }

            if (split == -1)
                for (int i = end; i > start + maxChars / 2; i--)
                    if (text[i] == ' ') { split = i + 1; break; }

            if (split == -1) split = end;

            var part = text[start..split].Trim();
            if (!string.IsNullOrWhiteSpace(part)) yield return part;

            // Step back ~overlapChars to find a sentence start for the next chunk
            int nextStart = split;
            if (overlapChars > 0 && split - overlapChars > start)
            {
                for (int i = split - 1; i > split - overlapChars; i--)
                {
                    if (i > 0 && text[i - 1] is '.' or '!' or '?' && text[i] == ' ')
                    { nextStart = i + 1; break; }
                }
                if (nextStart == split)
                    nextStart = Math.Max(start + 1, split - overlapChars);
            }
            start = nextStart;
        }
    }
}
