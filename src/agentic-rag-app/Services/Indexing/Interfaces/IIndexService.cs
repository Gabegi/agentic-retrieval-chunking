namespace ProtocolsIndexer.Services;

public interface IIndexService
{
    Task EnsureIndexAsync();
    Task<(long DocumentCount, long StorageSizeBytes)> GetStatisticsAsync(CancellationToken ct = default);
}
