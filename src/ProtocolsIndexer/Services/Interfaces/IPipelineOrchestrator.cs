namespace ProtocolsIndexer.Services;

public interface IPipelineOrchestrator
{
    Task RunAsync(CancellationToken ct = default);
    Task CompareAsync(CancellationToken ct = default);
}
