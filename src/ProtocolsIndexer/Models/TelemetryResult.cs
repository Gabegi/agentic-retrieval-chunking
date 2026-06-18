namespace ProtocolsIndexer.Models;

public record TelemetryResult(long LatencyMs, long InputTokens, long OutputTokens);
