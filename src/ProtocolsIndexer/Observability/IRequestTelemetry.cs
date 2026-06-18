namespace ProtocolsIndexer.Observability;

public interface IRequestTelemetry
{
    void Initialize();
    void AddTokens(long input, long output);
    TelemetryResult GetSummary(long latencyMs);
}
