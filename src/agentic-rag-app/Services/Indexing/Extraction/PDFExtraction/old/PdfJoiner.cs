using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// Merges data (pages) with metadata (index) — mirrors CsvJoiner, matching on BlobName
// instead of DOCUMENT_ID. No inactive-document branch: PDFs carry no active/inactive
// flag, so every matched page is kept.
//
// Potential outcomes
//     Matched            → AddJoined              → the only bucket that continues to PdfCleaner.
//     NotFound           → AddToNotFound           → tracked, not joined.
//     Index, zero pages  → AddToIndexWithoutPages  → tracked separately (expected: a PDF that
//                                                     failed to yield any page content).
public class PdfJoiner : IPdfJoiner
{
    private enum MatchStatus { NotFound, Matched }

    private readonly record struct PageMatch(string BlobName, MatchStatus Status, PdfJoinedPageRecord? Joined);

    public PdfJoinResult Join(IReadOnlyList<PdfPageRecord> pages, IReadOnlyList<PdfIndexRecord> index)
    {
        var result = new PdfJoinResult();

        var uniqueIndex = BuildIndexLookup(index, result);

        var matchedBlobNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var alreadyReported  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var page in pages)
        {
            var match = MatchPageWithIndexOnBlobName(page, uniqueIndex);
            SetPageResult(match, result, matchedBlobNames, alreadyReported);
        }

        // Iterate uniqueIndex.Values, not the raw index list - a duplicate BlobName that
        // never matches any page would otherwise get added to SkippedIndexRecords once
        // per duplicate row instead of once for the (deduplicated) document.
        foreach (var indexRecord in uniqueIndex.Values)
        {
            if (!matchedBlobNames.Contains(indexRecord.BlobName))
                result.AddToIndexWithoutPages(indexRecord);
        }

        return result;
    }

    // Matches pages with index based on BlobName. Page is dropped if no match is found.
    private static PageMatch MatchPageWithIndexOnBlobName(PdfPageRecord page, Dictionary<string, PdfIndexRecord> uniqueIndex)
    {
        if (!uniqueIndex.TryGetValue(page.BlobName, out var indexRecord))
            return new PageMatch(page.BlobName, MatchStatus.NotFound, Joined: null);

        return new PageMatch(page.BlobName, MatchStatus.Matched, ToJoinedRecord(page, indexRecord));
    }

    // Applies a PageMatch to the running PdfJoinResult and bookkeeping sets.
    private static void SetPageResult(
        PageMatch match, PdfJoinResult result, HashSet<string> matchedBlobNames, HashSet<string> alreadyReported)
    {
        switch (match.Status)
        {
            case MatchStatus.NotFound:
                ApplyNotFound(match, result, alreadyReported);
                break;

            case MatchStatus.Matched:
                matchedBlobNames.Add(match.BlobName);
                result.AddJoined(match.Joined!);
                break;
        }
    }

    // Not found -> log an error (once per blob, via alreadyReported). Page is dropped.
    private static void ApplyNotFound(PageMatch match, PdfJoinResult result, HashSet<string> alreadyReported)
    {
        if (alreadyReported.Add(match.BlobName))
            result.AddToNotFound(new JoinError
            {
                DocumentId = match.BlobName,
                Message    = $"No index record found for document {match.BlobName}.",
            });
    }

    private static PdfJoinedPageRecord ToJoinedRecord(PdfPageRecord page, PdfIndexRecord indexRecord) => new()
    {
        BlobName           = page.BlobName,
        PageIndex          = page.PageIndex,
        PageContent        = page.PageContent,
        Title              = indexRecord.Title,
        Version            = indexRecord.Version,
        PublicationDateRaw = indexRecord.PublicationDateRaw,
    };

    // Builds the BlobName -> PdfIndexRecord lookup Join matches pages against, warning
    // on (and skipping) any duplicate BlobName rather than throwing.
    private static Dictionary<string, PdfIndexRecord> BuildIndexLookup(IReadOnlyList<PdfIndexRecord> index, PdfJoinResult result)
    {
        var indexByBlobName = new Dictionary<string, PdfIndexRecord>(StringComparer.OrdinalIgnoreCase);
        var alreadyWarned   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in index)
        {
            if (!indexByBlobName.TryAdd(record.BlobName, record) && alreadyWarned.Add(record.BlobName))
            {
                result.AddToDuplicates(new JoinError
                {
                    DocumentId = record.BlobName,
                    Message    = $"Duplicate BlobName '{record.BlobName}' in index — kept the first occurrence.",
                });
            }
        }
        return indexByBlobName;
    }
}
