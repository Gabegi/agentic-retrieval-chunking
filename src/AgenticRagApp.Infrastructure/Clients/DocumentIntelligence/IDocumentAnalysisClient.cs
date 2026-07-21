using Azure;
using Azure.AI.DocumentIntelligence;

namespace AgenticRagApp.Infrastructure.Clients.DocumentIntelligence;

// Generic wrapper around DocumentIntelligenceClient — submits an analyze request and
// hands back the long-running Operation handle. Polling/backoff on that handle, and
// interpreting the eventual AnalyzeResult, are the caller's job (PDF-specific).
public interface IDocumentAnalysisClient
{
    Task<Operation<AnalyzeResult>> SubmitAnalyzeAsync(AnalyzeDocumentOptions options, CancellationToken ct = default);
}
