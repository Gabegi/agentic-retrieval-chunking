public interface IPipelineOrchestrator
{
    Task RunAsync(CancellationToken ct = default);
}
