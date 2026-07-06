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

        var uniqueIndex = CheckForDuplicateIDInIndex(index, result);

        var matchedDocIds  = new HashSet<string>();
        var alreadyErrored = new HashSet<string>();

        foreach (var page in pages)
            MatchPageToIndex(page, uniqueIndex, result, matchedDocIds, alreadyErrored);

        foreach (var indexRecord in index)
        {
            if (!matchedDocIds.Contains(indexRecord.DocumentId))
                result.AddSkippedIndexRecord(indexRecord);
        }

        return result;
    }

    // matches pages with index based on Document_id. Page is dropped if no match is
    // found, or if ACTIVE=False
    private static void MatchPageToIndex(
        PageRecord page, Dictionary<string, IndexRecord> uniqueIndex, JoinResult result,
        HashSet<string> matchedDocIds, HashSet<string> alreadyErrored)
    {
        if (uniqueIndex.TryGetValue(page.DocumentId, out var indexRecord))
        {
            // Matched an index record regardless of Active status — otherwise an
            // inactive-but-paged doc would fall into SkippedIndexRecords below and get
            // mislabeled as "no pages" when it's actually just excluded for being inactive.
            matchedDocIds.Add(page.DocumentId);

            if (!indexRecord.Active)
            {
                result.CountInactivePageSkipped();
                if (alreadyErrored.Add(page.DocumentId))   // reuse the per-doc dedup set
                    result.AddDataQualityWarning(new JoinError
                    {
                        DocumentId = page.DocumentId,
                        Message    = "Document is marked inactive in the index — pages skipped.",
                    });
                return;
            }

            result.AddJoined(new JoinedPageRecord
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
            });
        }
        else if (alreadyErrored.Add(page.DocumentId))
        {
            result.AddError(new JoinError
            {
                DocumentId = page.DocumentId,
                Message    = $"No index record found for document {page.DocumentId}.",
            });
        }
    }

    // Checks for duplicates in index by DOCUMENT_ID
    private static Dictionary<string, IndexRecord> CheckForDuplicateIDInIndex(IReadOnlyList<IndexRecord> index, JoinResult result)
    {
        var indexByDocId = new Dictionary<string, IndexRecord>();
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
