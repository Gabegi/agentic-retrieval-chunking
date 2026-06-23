namespace ProtocolsIndexer.Services;

public interface IPipelineOrchestrator
{
    Task ProcessBlobAsync(string blobName, byte[] bytes, CancellationToken ct = default);
    Task RunAsync(CancellationToken ct = default);
    Task CompareAsync(CancellationToken ct = default);
}
