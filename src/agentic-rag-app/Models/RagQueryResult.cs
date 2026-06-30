namespace ProtocolsIndexer.Models;

public record RagQueryResult(
    string Answer,
    string RetrievedContext,
    int    ChunksRetrieved,
    string Model,
    string FinishReason,
    long   LatencyMs,
    int    InputTokens,
    int    OutputTokens,
    int    TotalTokens);
