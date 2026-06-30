namespace ProtocolsIndexer.Observability.Reports;

public record QueryRunReport(
    string         RunId,
    DateTimeOffset Timestamp,
    string         Question,
    string         Answer,
    string         RetrievedContext,
    long           LatencyMs,
    int            InputTokens,
    int            OutputTokens);
