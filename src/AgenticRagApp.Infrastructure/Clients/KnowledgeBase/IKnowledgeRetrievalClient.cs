using Azure.Search.Documents.KnowledgeBases.Models;

namespace AgenticRagApp.Infrastructure.Clients.KnowledgeBase;

// Generic wrapper around KnowledgeBaseRetrievalClient (query-time retrieval — distinct
// from ISearchIndexStore's knowledge source/base *admin* methods, which are a different
// client). Callers build the request and interpret the response.
public interface IKnowledgeRetrievalClient
{
    Task<KnowledgeBaseRetrievalResponse> RetrieveAsync(KnowledgeBaseRetrievalRequest request, CancellationToken ct = default);
}
