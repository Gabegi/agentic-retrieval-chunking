using System.Text.RegularExpressions;

namespace ProtocolsIndexer.Utils;

public record TextFidelityResult(
    List<string> LikelyMergedWords,
    List<string> MojibakeFound,
    int ReplacementCharacterCount,
    List<string> DroppedDiacritics)
{
    public int TotalIssues =>
        LikelyMergedWords.Count + MojibakeFound.Count + ReplacementCharacterCount + DroppedDiacritics.Count;
}

public static class TextFidelityChecker
{
    // ── Vocabulary sources ──────────────────────────────────────────
    private static readonly HashSet<string> DutchWordList = LoadOpenTaalDictionary("nl_NL.dic");

    private static readonly HashSet<string> KnownAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "RIVM", "GGD", "ECDC", "WHO", "CDC", "LCI", "BSL", "PCR", "IgM", "IgG",
        "HCPS", "ANDV", "EDTA", "FFP2", "TSH", "FT4", "ANA"
    };

    private static readonly string[] TaxonomicSuffixes =
        { "virus", "coccus", "bacterium", "ella", "monas" };

    // ── Character-integrity sources ───────────────────────────────────
    private static readonly Dictionary<string, string> KnownMojibake = new()
    {
        ["Ã«"] = "ë", ["Ã©"] = "é", ["Ã¯"] = "ï", ["Ã¼"] = "ü", ["â€™"] = "'"
    };

    private static readonly Dictionary<string, string> KnownDiacriticTerms = new()
    {
        ["geinformeerde"] = "geïnformeerde",
        ["coefficient"]   = "coëfficiënt",
        ["ideeen"]        = "ideeën",
    };

    // ── Public entry point ────────────────────────────────────────────
    public static TextFidelityResult Check(string text) => new(
        FindLikelyMergedWords(text),
        FindMojibakePatterns(text),
        CountReplacementCharacters(text),
        FindDroppedDiacritics(text));

    // ── Vocabulary validation ─────────────────────────────────────────

    private static List<string> FindLikelyMergedWords(string text)
    {
        var suspects = new List<string>();
        var rawWords = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var trimmed = rawWords.Select(w => w.Trim('.', ',', ':', ';', '(', ')')).ToArray();

        for (int i = 0; i < trimmed.Length; i++)
        {
            var word = trimmed[i];
            var lower = word.ToLowerInvariant();

            if (word.Length < 16 || IsKnownWord(lower)) continue;

            // Skip Latin binomial pairs — e.g. "Oligoryzomys longicaudatus"
            if (i < trimmed.Length - 1 && IsBinomialPair(word, trimmed[i + 1]))
                continue;

            // Try every split point — both halves real words means it's a merge,
            // e.g. "richtlijnwerkgroep" + "heeft" stuck together with no space
            bool isMerge = false;
            for (int split = 4; split < lower.Length - 4; split++)
            {
                if (IsKnownWord(lower[..split]) && IsKnownWord(lower[split..]))
                {
                    isMerge = true;
                    break;
                }
            }

            if (isMerge) suspects.Add(word);
        }

        return suspects;
    }


    private static bool IsKnownWord(string word) =>
        DutchWordList.Contains(word)
        || KnownAbbreviations.Contains(word)
        || TaxonomicSuffixes.Any(s => word.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    private static bool IsBinomialPair(string first, string second) =>
        first.Length > 0 && second.Length > 0
        && char.IsUpper(first[0]) && char.IsLower(second[0])
        && !IsKnownWord(first.ToLowerInvariant())
        && !IsKnownWord(second.ToLowerInvariant());

    // ── Character integrity ───────────────────────────────────────────

    private static List<string> FindMojibakePatterns(string text) =>
        KnownMojibake.Keys
            .Where(pattern => Regex.IsMatch(text, Regex.Escape(pattern)))
            .Select(pattern => $"{pattern} → should be {KnownMojibake[pattern]}")
            .ToList();

    private static int CountReplacementCharacters(string text) =>
        text.Count(c => c == '�');

    private static List<string> FindDroppedDiacritics(string text)
    {
        var lower = text.ToLowerInvariant();
        return KnownDiacriticTerms
            .Where(kv => lower.Contains(kv.Key) && !lower.Contains(kv.Value.ToLowerInvariant()))
            .Select(kv => $"{kv.Key} → should be {kv.Value}")
            .ToList();
    }

    // ── Dictionary loading ────────────────────────────────────────────

    private static HashSet<string> LoadOpenTaalDictionary(string path)
    {
        if (!File.Exists(path))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Hunspell .dic format: "word/affixflags" — strip everything after '/'
        return new HashSet<string>(
            File.ReadAllLines(path).Select(line => line.Split('/')[0].Trim()),
            StringComparer.OrdinalIgnoreCase);
    }
}
