using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// Merges data (pages) with metadata (index).
//
// Page-level content (zenya_pages.csv, one row per page) with document-level
// metadata (zenya_index.csv, one row per document) by DOCUMENT_ID

// neither file alone has everything a page needs to be indexed. Also classifies every way the two files
// can disagree: matched+active proceeds to DataCleaner; matched-but-inactive skips the
// page (DataQualityWarning); 

// a page with no matching index record is an Error; an
// index record with no matching pages is tracked separately (SkippedIndexRecords); a
// duplicate DOCUMENT_ID in the index is a DataQualityWarning (first occurrence wins).
public static class CsvJoiner
{
    public static JoinResult Join(IReadOnlyList<PageRecord> pages, IReadOnlyList<IndexRecord> index)
    {
        var result = new JoinResult();

        var uniqueIndex = FindUniqueIDinIndex(index, result);

        // OrdinalIgnoreCase: we don't have confirmation that DOCUMENT_ID values are
        // consistently cased across the two source files (see docs/data-questions.md).
        // If they're always consistent this changes nothing; if they're not, it
        // prevents the same silent-mismatch failure mode Trim() already guards
        // against for whitespace in RequireDocumentId.
        var matchedDocIds   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var alreadyReported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var page in pages)
            MatchPageToIndex(page, uniqueIndex, result, matchedDocIds, alreadyReported);

        // Iterate uniqueIndex.Values, not the raw index list - a duplicate DOCUMENT_ID
        // that never matches any page would otherwise get added to SkippedIndexRecords
        // once per duplicate row instead of once for the (deduplicated) document.
        foreach (var indexRecord in uniqueIndex.Values)
        {
            if (!matchedDocIds.Contains(indexRecord.DocumentId))
                result.AddSkippedIndexRecord(indexRecord);
        }

        return result;
    }

    // matches pages with index based on Document_id.
    // Page is dropped if no match is found, or if ACTIVE=False
    private static PageMatch MatchPageToIndexBasedOnId(PageRecord page, Dictionary<string, IndexRecord> uniqueIndex)
    {
        if (!uniqueIndex.TryGetValue(page.DocumentId, out var indexRecord))
            return new PageMatch(page.DocumentId, MatchStatus.NotFound, Joined: null);

        if (!indexRecord.Active)
            return new PageMatch(page.DocumentId, MatchStatus.Inactive, Joined: null);

        return new PageMatch(page.DocumentId, MatchStatus.Matched, ToJoinedRecord(page, indexRecord));
    }

    // Applies a PageMatch to the running JoinResult and bookkeeping sets.
    // Split from MatchPageToIndex so the lookup logic stays a pure function of (page, index).
    private static void ApplyMatch(
        PageMatch match, JoinResult result, HashSet<string> matchedDocIds, HashSet<string> alreadyReported)
    {
        switch (match.Status)
        {
            case MatchStatus.NotFound:
                ApplyNotFound(match, result, alreadyReported);
                break;

            case MatchStatus.Inactive:
                // Mark this index record as "used" regardless of Active,
                // so it won't wrongly show up as unmatched later.
                matchedDocIds.Add(match.DocumentId);
                ApplyInactive(match, result, alreadyReported);
                break;

            case MatchStatus.Matched:
                matchedDocIds.Add(match.DocumentId);
                result.AddJoined(match.Joined!);
                break;
        }
    }

    // Not found -> log an error (once per doc, via alreadyReported). Page is dropped.
    private static void ApplyNotFound(PageMatch match, JoinResult result, HashSet<string> alreadyReported)
    {
        if (alreadyReported.Add(match.DocumentId))
            result.AddError(new JoinError
            {
                DocumentId = match.DocumentId,
                Message    = $"No index record found for document {match.DocumentId}.",
            });
    }

    // Inactive -> count as skipped, warn once per doc (via alreadyReported). Page is dropped.
    private static void ApplyInactive(PageMatch match, JoinResult result, HashSet<string> alreadyReported)
    {
        result.CountInactivePageSkipped();
        if (alreadyReported.Add(match.DocumentId))
            result.AddDataQualityWarning(new JoinError
            {
                DocumentId = match.DocumentId,
                Message    = "Document is marked inactive in the index — pages skipped.",
            });
    }

    private static JoinedPageRecord ToJoinedRecord(PageRecord page, IndexRecord indexRecord) => new()
    {
        DocumentId        = page.DocumentId,
        Title             = page.Title,
        QuickCode         = page.QuickCode,
        FolderPath        = page.FolderPath,
        LastModifiedRaw   = page.LastModifiedRaw,
        PageIndex         = page.PageIndex,
        PageContent       = page.PageContent,
        Language          = page.Language,
        RelativePath      = page.RelativePath,
        DocumentTypeName  = indexRecord.DocumentTypeName,
        Summary           = indexRecord.Summary,
        Version           = indexRecord.Version,
        CheckDateRaw      = indexRecord.CheckDateRaw,
        AttentionFlagsRaw = indexRecord.AttentionFlagsRaw,
    };

    // Builds the DOCUMENT_ID -> IndexRecord lookup Join matches pages against, warning
    // on (and skipping) any duplicate DOCUMENT_ID rather than throwing.
    private static Dictionary<string, IndexRecord> FindUniqueIDinIndex(IReadOnlyList<IndexRecord> index, JoinResult result)
    {
        var indexByDocId = new Dictionary<string, IndexRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in index)
        {
            if (!indexByDocId.TryAdd(record.DocumentId, record))
            {
                result.AddDataQualityWarning(new JoinError
                {
                    DocumentId = record.DocumentId,
                    Message    = $"Duplicate DOCUMENT_ID '{record.DocumentId}' in index — kept the first occurrence.",
                });
            }
        }
        return indexByDocId;
    }
}
