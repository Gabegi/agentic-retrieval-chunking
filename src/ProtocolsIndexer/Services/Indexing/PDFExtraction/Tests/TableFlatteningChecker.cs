using System.Text.RegularExpressions;

namespace ProtocolsIndexer.Utils;

public record TableFlatteningResult(string DocId, string Heading, List<string> RepeatedPhrases)
{
    public bool LooksFlattened => RepeatedPhrases.Count > 0;
}

// Detects tables that were collapsed into unstructured prose during extraction.
// Signal: a 3-word phrase that repeats within a single chunk — a strong indicator
// that structured row data was run together with no delimiters left.
public static class TableFlatteningChecker
{
    // ── Public entry point ────────────────────────────────────────────

    /// <summary>Returns only chunks where at least one trigram repeats.</summary>
    public static List<TableFlatteningResult> Check(IEnumerable<Chunk> chunks)
    {
        var results = new List<TableFlatteningResult>();

        foreach (var chunk in chunks)
        {
            var repeated = FindRepeatedTrigrams(chunk.Text);
            if (repeated.Count > 0)
                results.Add(new TableFlatteningResult(chunk.DocId, chunk.Heading, repeated));
        }

        return results;
    }

    // ── Trigram analysis ──────────────────────────────────────────────

    private static List<string> FindRepeatedTrigrams(string text)
    {
        var words = Tokenize(text);
        if (words.Length < 3)
            return [];

        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i <= words.Length - 3; i++)
        {
            var trigram = $"{words[i]} {words[i + 1]} {words[i + 2]}";
            seen[trigram] = seen.TryGetValue(trigram, out var count) ? count + 1 : 1;
        }

        return seen
            .Where(kv => kv.Value > 1)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"\"{kv.Key}\" ({kv.Value}x)")
            .ToList();
    }

    private static string[] Tokenize(string text) =>
        Regex.Replace(text.ToLowerInvariant(), @"[^\w\s]", " ")
             .Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
