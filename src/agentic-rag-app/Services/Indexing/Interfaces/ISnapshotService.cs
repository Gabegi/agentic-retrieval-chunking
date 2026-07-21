using AgenticRag.Models;

namespace AgenticRag.Services;

// Maintains the rolling full-corpus snapshot (indexing-artifacts/snapshots/{instanceId}/full-index.json)
// and, as a direct follow-on, evicts vector-cache entries the snapshot no longer references.
//
// IMPORTANT operational note: the snapshot only ever gains chunks that actually pass through
// UpdateAsync (i.e. new/updated docs from a normal incremental run). A document that was
// already indexed before this feature existed, and is never updated again, will never appear
// in any snapshot. Run a `force=true` reindex once after deploying this so the first snapshot
// captures a complete baseline - after that, incremental runs keep it complete via the merge.
public interface ISnapshotService
{
    // newChunks: this run's freshly processed chunks (the same ones just sent to Search).
    // staleDocumentIds: ExtractionResults.StaleDocumentIds - document ids (updated + removed)
    // whose old snapshot entries must be dropped before the new ones are merged in.
    Task UpdateAsync(
        IReadOnlyList<DocumentChunk> newChunks,
        IReadOnlyList<string>           staleDocumentIds,
        string                          instanceId,
        CancellationToken               ct = default);
}
