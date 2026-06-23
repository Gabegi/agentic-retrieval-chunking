namespace ProtocolsIndexer.Observability;

// Singleton — uses AsyncLocal so state is scoped to each async invocation chain
public class RequestTelemetry : IRequestTelemetry
{
    private static readonly AsyncLocal<State?> _current = new();

    public void Initialize() => _current.Value = new State();

    public void AddTokens(long input, long output)
    {
        if (_current.Value is not { } s) return;
        Interlocked.Add(ref s.InputTokens, input);
        Interlocked.Add(ref s.OutputTokens, output);
    }

    public TelemetryResult GetSummary(long latencyMs)
    {
        var s = _current.Value ?? new State();
        return new(latencyMs, s.InputTokens, s.OutputTokens);
    }

    private sealed class State
    {
        public long InputTokens;
        public long OutputTokens;
    }
}
