using Azure;
using Azure.AI.DocumentIntelligence;

namespace AgenticRagApp.Infrastructure.Clients.DocumentIntelligence;

public class DocumentAnalysisClient : IDocumentAnalysisClient
{
    private readonly DocumentIntelligenceClient _client;

    public DocumentAnalysisClient(DocumentIntelligenceClient client) => _client = client;

    public async Task<Operation<AnalyzeResult>> SubmitAnalyzeAsync(AnalyzeDocumentOptions options, CancellationToken ct = default) =>
        await _client.AnalyzeDocumentAsync(WaitUntil.Started, options, cancellationToken: ct);
}
