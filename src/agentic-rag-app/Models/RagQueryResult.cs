namespace ProtocolsIndexer.Models;

public record RagQueryResult(
    string Answer,
    string RetrievedContext,
    string SystemInstructions,
    int    ChunksRetrieved,
    string OperationName,
    string ProviderName,
    string ServerAddress,
    int    ServerPort,
    string Model,
    string FinishReason,
    long   LatencyMs,
    int    InputTokens,
    int    OutputTokens,
    int    TotalTokens);
