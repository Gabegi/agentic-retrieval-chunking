namespace ProtocolsIndexer.Observability.Reports;

public record QueryRunReport(
    string         RunId,
    DateTimeOffset Timestamp,
    string         Question,
    string         Answer,
    string         RetrievedContext,
    int            ChunksRetrieved,
    string         Model,
    string         FinishReason,
    long           LatencyMs,
    int            InputTokens,
    int            OutputTokens,
    int            TotalTokens);
