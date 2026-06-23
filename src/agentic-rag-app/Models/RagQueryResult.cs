namespace ProtocolsIndexer.Models;

public record RagQueryResult(
    string Answer,
    string RetrievedContext,
    long   LatencyMs,
    int    InputTokens,
    int    OutputTokens);
