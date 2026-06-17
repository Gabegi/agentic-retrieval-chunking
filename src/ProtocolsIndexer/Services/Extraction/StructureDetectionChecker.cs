using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Utils;

public record StructureDetectionResult(
    int KnownSectionsFound,
    int KnownSectionsExpected,
    int HeadingsFlagged,
    int HeadingsLikelyReal,
    List<string> SuspiciousHeadings,
    List<string> ChunksWithFlattenedTables)
{
    public double HeadingRecall    => KnownSectionsExpected > 0 ? (double)KnownSectionsFound / KnownSectionsExpected : 0;
    public double HeadingPrecision => HeadingsFlagged > 0 ? (double)HeadingsLikelyReal / HeadingsFlagged : 0;
}

public static class StructureDetectionChecker
{
    private static readonly HashSet<string> KnownSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "Samenvatting", "Ziekteverschijnselen", "Verwekker", "Epidemiologie",
        "Reservoir", "Besmettingsweg", "Incubatieperiode", "Besmettelijke periode",
        "Diagnostiek", "Behandeling", "Preventie", "Maatregelen", "Meldingsplicht",
        "Bronopsporing", "Contactonderzoek", "Desinfectie", "Risicogroepen",
        "Achtergrondinformatie", "Arbeidsrelevante aanvullingen", "Veterinaire informatie",
        "Literatuur"
    };

    // Sentence markers — a real heading is a noun phrase, not a sentence
    private static readonly HashSet<string> SentenceVerbMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "wordt", "worden", "is", "zijn", "kan", "kunnen", "moet", "moeten",
        "dient", "dienen", "geldt", "gelden", "betreft", "heeft", "hebben"
    };

    public static StructureDetectionResult Check(List<ProtocolDocument> chunks)
    {
        // Reassemble a proxy for the full document text from the chunks themselves —
        // no need to thread a separate raw-text parameter through the extraction services
        var fullText = string.Join(" ", chunks.Select(c => $"{c.Heading} {c.Content}"));

        var detectedHeadings = chunks
            .Where(c => c.Heading != null)
            .Select(c => CleanHeading(c.Heading!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // ── Recall: of known sections that appear somewhere in the text,
        // how many were actually detected as a Heading? ──────────────────────
        var expectedSections = KnownSections
            .Where(s => fullText.Contains(s, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var foundSections = expectedSections.Count(s => detectedHeadings.Contains(s));

        // ── Precision: of everything flagged as a heading, how many look real? ─
        var flagged = chunks.Where(c => c.Heading != null).Select(c => c.Heading!).ToList();
        var suspicious = flagged.Where(LooksLikeBodyTextNotHeading).ToList();

        // ── Table flattening: a 3-word phrase repeating inside one chunk ──────
        var flattenedTables = chunks
            .Where(c => HasRepeatedPhrase(c.Content))
            .Select(c => c.Id)
            .ToList();

        return new StructureDetectionResult(
            foundSections, expectedSections.Count,
            flagged.Count, flagged.Count - suspicious.Count,
            suspicious, flattenedTables);
    }

    private static bool LooksLikeBodyTextNotHeading(string heading)
    {
        var clean = CleanHeading(heading);
        var wordCount = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        int strikes = 0;
        if (wordCount > 6) strikes++;
        if (".!?".Contains(clean.TrimEnd().LastOrDefault())) strikes++;
        if (clean.Split(' ').Any(w => SentenceVerbMarkers.Contains(w.Trim('.', ',')))) strikes++;
        if (System.Text.RegularExpressions.Regex.IsMatch(heading, @"^[\(\)\.,]+")) strikes++;

        return strikes >= 2;
    }

    private static string CleanHeading(string heading) =>
        System.Text.RegularExpressions.Regex.Replace(heading, @"^[\(\)\.,]+", "").Trim();

    private static bool HasRepeatedPhrase(string content)
    {
        var words = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var seen = new HashSet<string>();

        for (int i = 0; i <= words.Length - 3; i++)
        {
            var phrase = string.Join(' ', words.Skip(i).Take(3)).ToLowerInvariant();
            if (!seen.Add(phrase)) return true;
        }
        return false;
    }
}