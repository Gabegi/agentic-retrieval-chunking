using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.KnowledgeBases;
using Azure.Search.Documents.KnowledgeBases.Models;

namespace AgenticRagApp.Infrastructure.Clients.Search;

// Generic wrapper around SearchIndexClient — index schema admin plus knowledge
// source/base admin (both exposed on the same underlying client). Callers build the
// SearchIndex/SearchIndexKnowledgeSource/KnowledgeBase definitions themselves (that's
// the doc-type-specific part); this only ever persists whatever definition it's given.
public interface ISearchIndexStore
{
    // Get-or-create only — never updates an existing index (avoids a code-driven push
    // silently overwriting portal-side customisation). Returns true if it was created,
    // false if it already existed.
    Task<bool> EnsureIndexAsync(SearchIndex definition, CancellationToken ct = default);

    Task<(long DocumentCount, long StorageSizeBytes)> GetStatisticsAsync(string indexName, CancellationToken ct = default);

    Task CreateOrUpdateKnowledgeSourceAsync(SearchIndexKnowledgeSource source, CancellationToken ct = default);

    Task CreateOrUpdateKnowledgeBaseAsync(KnowledgeBase knowledgeBase, CancellationToken ct = default);
}
