namespace ProtocolsIndexer.Observability.Reports;

public record QueryRunReport(
    string         RunId,
    DateTimeOffset Timestamp,
    string         Question,
    string         Answer,
    string         RetrievedContext,
    string         SystemInstructions,
    int            ChunksRetrieved,
    string         OperationName,
    string         ProviderName,
    string         ServerAddress,
    int            ServerPort,
    string         Model,
    string         FinishReason,
    long           LatencyMs,
    int            InputTokens,
    int            OutputTokens,
    int            TotalTokens);
