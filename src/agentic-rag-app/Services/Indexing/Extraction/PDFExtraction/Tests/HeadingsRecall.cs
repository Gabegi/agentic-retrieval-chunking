using System.Text.RegularExpressions;

namespace ProtocolsIndexer.Utils;

public record Chunk(string DocId, string Heading, string Text);

public record RecallResult(string DocId, int TotalTocEntries, int Matched, List<string> Missing)
{
    public double Recall => TotalTocEntries == 0 ? 1.0 : Matched / (double)TotalTocEntries;
}

// Ground truth = TOC chunk (Heading == "Inhoudsopgave"), split on '·'.
// Recall is computed per document. Extra headings never lower the score —
// that's precision's job, which we're not bothering with.
public static class HeadingRecallChecker
{
    private const string TocHeading = "Inhoudsopgave";
    private const char TocDelimiter = '·';

    // ── Public entry point ────────────────────────────────────────────

    public static List<RecallResult> CheckRecall(IEnumerable<Chunk> allChunks)
    {
        var results = new List<RecallResult>();

        foreach (var docGroup in allChunks.GroupBy(c => c.DocId))
        {
            var docChunks = docGroup.ToList();
            var tocChunk = docChunks.FirstOrDefault(c => c.Heading == TocHeading);
            if (tocChunk is null)
                continue;

            var tocEntries = tocChunk.Text
                .Split(TocDelimiter)
                .Select(Normalize)
                .Where(s => s.Length > 0)
                .Distinct()
                .ToList();

            var extractedHeadings = docChunks
                .Where(c => c.Heading != TocHeading)
                .Select(c => Normalize(c.Heading))
                .ToHashSet();

            var missing = new List<string>();
            int matched = 0;
            foreach (var entry in tocEntries)
            {
                if (extractedHeadings.Contains(entry)) matched++;
                else missing.Add(entry);
            }

            results.Add(new RecallResult(docGroup.Key, tocEntries.Count, matched, missing));
        }

        return results;
    }

    // ── Normalization ─────────────────────────────────────────────────

    // TOC text and in-body headings rarely match character-for-character —
    // strip trailing page numbers ("...... 12"), leading numbering ("2.3 "),
    // then lowercase + trim. Without this, exact equality undercounts recall.
    private static string Normalize(string text)
    {
        var s = text.Trim();
        s = Regex.Replace(s, @"[\.\s]*\d+\s*$", "");
        s = Regex.Replace(s, @"^\d+(\.\d+)*\.?\s*", "");
        return s.Trim().ToLowerInvariant();
    }
}
