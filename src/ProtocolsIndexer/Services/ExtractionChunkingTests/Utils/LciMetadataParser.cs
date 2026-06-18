using System.Text.RegularExpressions;

namespace ProtocolsIndexer.Utils;

public record DocumentMetadata(string RichtlijnName, string? PublicationDate, string? Version);

public static class LciMetadataParser
{
    private static readonly Regex DutchDateRegex = new(
        @"\b(\d{1,2}[-\s](januari|februari|maart|april|mei|juni|juli|augustus|" +
        @"september|oktober|november|december)[-\s]\d{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ShortDateRegex  = new(@"\b(\d{2}-\d{2}-\d{4})\b",      RegexOptions.Compiled);
    private static readonly Regex VersionRegex     = new(@"[Vv]ersie\s+([\d.]+)",          RegexOptions.Compiled);
    private static readonly Regex TitleRegex       = new(@"^(.+?)\s*\|\s*LCI-richtlijn",  RegexOptions.Multiline | RegexOptions.Compiled);

    public static DocumentMetadata Parse(string text, string blobName)
    {
        var richtlijnName = blobName.Split('/')[0]
            .Replace("lci_", "", StringComparison.OrdinalIgnoreCase)
            .Replace("-", " ");

        string? date    = null;
        string? version = null;

        var dateMatch = DutchDateRegex.Match(text);
        if (dateMatch.Success)
            date = dateMatch.Value;
        else
        {
            var shortMatch = ShortDateRegex.Match(text);
            if (shortMatch.Success) date = shortMatch.Value;
        }

        var versionMatch = VersionRegex.Match(text);
        if (versionMatch.Success) version = versionMatch.Groups[1].Value;

        var titleMatch = TitleRegex.Match(text);
        if (titleMatch.Success) richtlijnName = titleMatch.Groups[1].Value.Trim();

        return new DocumentMetadata(richtlijnName, date, version);
    }
}
