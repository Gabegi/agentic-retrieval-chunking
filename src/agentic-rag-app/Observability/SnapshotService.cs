using System.Text.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using AgenticRag.Models;
using AgenticRag.Observability.Reports;

namespace AgenticRag.Services;

public class SnapshotService : ISnapshotService
{
    private const string Prefix = "snapshots";
    private const string FileName = "full-index.json";

    // Keep the 3 most recent snapshots - an explicit exception to the archive's otherwise
    // "keep forever" retention (see docs/plan210726.md), scoped only to this path.
    private const int MaxRetainedSnapshots = 3;

    private readonly BlobContainerClient      _container;
    private readonly IVectorCache             _vectorCache;
    private readonly ILogger<SnapshotService> _logger;

    public SnapshotService(BlobContainerClient container, IVectorCache vectorCache, ILogger<SnapshotService> logger)
    {
        _container   = container;
        _vectorCache = vectorCache;
        _logger      = logger;
    }

    public async Task UpdateAsync(
        IReadOnlyList<ProtocolDocument> newChunks,
        IReadOnlyList<string>           staleDocumentIds,
        string                          instanceId,
        CancellationToken               ct = default)
    {
        // Newest-first, so [0] is "the previous snapshot" to merge from, and everything from
        // MaxRetainedSnapshots-1 onward is what gets pruned once this run's new one is added.
        var existing = await ListSnapshotBlobsAsync(ct);

        var previous = existing.Count > 0
            ? await ReadSnapshotAsync(existing[0].Path, ct)
            : [];

        // Drop old entries for any document this run touched (updated or removed), then add
        // this run's fresh chunks. A document untouched this run keeps its previous entry
        // unchanged - that's how the snapshot accumulates into a full-corpus picture over time.
        var staleSet = new HashSet<string>(staleDocumentIds, StringComparer.OrdinalIgnoreCase);
        var merged = previous
            .Where(c => !staleSet.Contains(c.DocumentId))
            .Concat(newChunks.Select(SnapshotChunk.From))
            .ToList();

        var path = $"{Prefix}/{instanceId}/{FileName}";
        await WriteAsync(path, merged, ct);
        _logger.LogInformation("Snapshot written — {Count} chunks → {Path}", merged.Count, path);

        var prunedCount = await PruneAsync(existing, ct);
        if (prunedCount > 0)
            _logger.LogInformation("Snapshot pruning — {Count} older snapshot(s) deleted", prunedCount);

        var liveHashes = merged.Select(c => c.ContentHash).ToHashSet();
        var evictedCount = await _vectorCache.EvictOrphanedAsync(liveHashes, ct);
        if (evictedCount > 0)
            _logger.LogInformation("Vector cache eviction — {Count} orphaned entr{Suffix} deleted",
                evictedCount, evictedCount == 1 ? "y" : "ies");
    }

    private async Task<List<(string Path, DateTimeOffset LastModified)>> ListSnapshotBlobsAsync(CancellationToken ct)
    {
        var results = new List<(string Path, DateTimeOffset LastModified)>();

        await foreach (var blobItem in _container.GetBlobsAsync(BlobTraits.None, BlobStates.None, $"{Prefix}/", ct))
        {
            if (!blobItem.Name.EndsWith($"/{FileName}", StringComparison.Ordinal)) continue;
            results.Add((blobItem.Name, blobItem.Properties.LastModified ?? DateTimeOffset.MinValue));
        }

        return results.OrderByDescending(r => r.LastModified).ToList();
    }

    // Keeps the newest (MaxRetainedSnapshots - 1) of the pre-existing snapshots - one slot is
    // already spoken for by the new snapshot UpdateAsync just wrote - and deletes the rest.
    private async Task<int> PruneAsync(List<(string Path, DateTimeOffset LastModified)> existing, CancellationToken ct)
    {
        var toDelete = existing.Skip(MaxRetainedSnapshots - 1).ToList();
        foreach (var (path, _) in toDelete)
            await _container.GetBlobClient(path).DeleteIfExistsAsync(cancellationToken: ct);

        return toDelete.Count;
    }

    private async Task<List<SnapshotChunk>> ReadSnapshotAsync(string path, CancellationToken ct)
    {
        try
        {
            var download = await _container.GetBlobClient(path).DownloadContentAsync(ct);
            return JsonSerializer.Deserialize<List<SnapshotChunk>>(download.Value.Content.ToMemory().Span) ?? [];
        }
        catch (Exception ex) when (ex is Azure.RequestFailedException or JsonException)
        {
            // Missing/corrupt previous snapshot shouldn't block this run - starts the merge
            // from empty, same as the very first run ever. Self-corrects over subsequent runs.
            _logger.LogWarning(ex, "Failed to read previous snapshot '{Path}' — starting merge from empty.", path);
            return [];
        }
    }

    private async Task WriteAsync(string path, List<SnapshotChunk> chunks, CancellationToken ct)
    {
        await _container.CreateIfNotExistsAsync(cancellationToken: ct);
        var json = JsonSerializer.SerializeToUtf8Bytes(chunks);
        using var ms = new MemoryStream(json);
        await _container.GetBlobClient(path).UploadAsync(ms, overwrite: true, cancellationToken: ct);
    }
}
