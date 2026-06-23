using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public static class CsvJoiner
{
    public static JoinResult Join(IReadOnlyList<PageRecord> pages, IReadOnlyList<IndexRecord> index)
    {
        var result = new JoinResult();

        // TryAdd instead of ToDictionary — duplicate DOCUMENT_ID gets warned instead of crashing.
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

        var matchedDocIds  = new HashSet<string>();
        var alreadyErrored = new HashSet<string>();

        foreach (var page in pages)
        {
            if (indexByDocId.TryGetValue(page.DocumentId, out var indexRecord))
            {
                matchedDocIds.Add(page.DocumentId);
                result.AddJoined(new JoinedPageRecord
                {
                    DocumentId        = page.DocumentId,
                    Title             = page.Title,
                    QuickCode         = page.QuickCode,
                    FolderPath        = page.FolderPath,
                    LastModifiedRaw   = page.LastModifiedRaw,
                    PageIndex         = page.PageIndex,
                    PageContent       = page.PageContent,
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

        foreach (var indexRecord in index)
        {
            if (!matchedDocIds.Contains(indexRecord.DocumentId))
                result.AddSkippedIndexRecord(indexRecord);
        }

        return result;
    }
}
