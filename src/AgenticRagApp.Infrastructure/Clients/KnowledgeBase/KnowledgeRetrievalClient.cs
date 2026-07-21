using Azure.Search.Documents.KnowledgeBases;
using Azure.Search.Documents.KnowledgeBases.Models;

namespace AgenticRagApp.Infrastructure.Clients.KnowledgeBase;

public class KnowledgeRetrievalClient : IKnowledgeRetrievalClient
{
    private readonly KnowledgeBaseRetrievalClient _client;

    public KnowledgeRetrievalClient(KnowledgeBaseRetrievalClient client) => _client = client;

    public async Task<KnowledgeBaseRetrievalResponse> RetrieveAsync(KnowledgeBaseRetrievalRequest request, CancellationToken ct = default)
    {
        var response = await _client.RetrieveAsync(request, cancellationToken: ct);
        return response.Value;
    }
}
