using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public interface IRequestTelemetry
{
    void Initialize();
    void AddTokens(long input, long output);
    TelemetryResult GetSummary(long latencyMs);
}
