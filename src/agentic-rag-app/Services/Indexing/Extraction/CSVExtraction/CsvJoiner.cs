using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// Merges data (pages) with metadata (index).
//
// Page-level content (zenya_pages.csv, one row per page) with document-level
// metadata (zenya_index.csv, one row per document) by DOCUMENT_ID - neither file alone
// has everything a page needs to be indexed. Also classifies every way the two files
// can disagree: matched+active proceeds to DataCleaner; matched-but-inactive skips the
// page (DataQualityWarning); a page with no matching index record is an Error; an
// index record with no matching pages is tracked separately (SkippedIndexRecords); a
// duplicate DOCUMENT_ID in the index is a DataQualityWarning (first occurrence wins).
public static class CsvJoiner
{
    public static JoinResult Join(IReadOnlyList<PageRecord> pages, IReadOnlyList<IndexRecord> index)
    {
        var result = new JoinResult();

        var uniqueIndex = BuildUniqueIndex(index, result);

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
    private static void MatchPageToIndex(
        PageRecord page, Dictionary<string, IndexRecord> uniqueIndex, JoinResult result,
        HashSet<string> matchedDocIds, HashSet<string> alreadyReported)
    {
        // Look up page.DocumentId in uniqueIndex.
        if (!uniqueIndex.TryGetValue(page.DocumentId, out var indexRecord))
        {
            // Not found -> log an error (once per doc, via alreadyReported), then return - page is dropped.
            if (alreadyReported.Add(page.DocumentId))
                result.AddError(new JoinError
                {
                    DocumentId = page.DocumentId,
                    Message    = $"No index record found for document {page.DocumentId}.",
                });
            return;
        }

        // Found -> mark this index record as "used" regardless of Active,
        // so it won't wrongly show up as unmatched later.
        matchedDocIds.Add(page.DocumentId);

        if (!indexRecord.Active)
        {
            // Inactive -> count as skipped, warn once per doc, return - page dropped.
            result.CountInactivePageSkipped();
            if (alreadyReported.Add(page.DocumentId))
                result.AddDataQualityWarning(new JoinError
                {
                    DocumentId = page.DocumentId,
                    Message    = "Document is marked inactive in the index — pages skipped.",
                });
            return;
        }

        // Active -> build a JoinedPageRecord and add it to result.
        result.AddJoined(ToJoinedRecord(page, indexRecord));
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
    private static Dictionary<string, IndexRecord> BuildUniqueIndex(IReadOnlyList<IndexRecord> index, JoinResult result)
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
