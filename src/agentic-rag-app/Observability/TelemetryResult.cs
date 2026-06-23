namespace ProtocolsIndexer.Observability;

public record TelemetryResult(long LatencyMs, long InputTokens, long OutputTokens);
