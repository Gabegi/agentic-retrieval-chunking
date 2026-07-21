namespace AgenticRag.Services;

// Content-hash-keyed store of already-computed embedding vectors. Exists because the Search
// index's content_vector field is IsStored=false - not retrievable from Search once written -
// so this is the only place a previously-computed vector can be read back from, letting
// EmbeddingService skip the paid API call for a chunk whose content hasn't changed.
public interface IVectorCache
{
    // Null on a miss - never embedded before, or the cached entry is missing/corrupt.
    // Callers should still sanity-check the returned vector's length against the current
    // embedding config before trusting it (a model/dimension change can leave stale entries).
    Task<float[]?> TryGetAsync(string contentHash, CancellationToken ct = default);

    Task SetAsync(string contentHash, float[] vector, CancellationToken ct = default);
}
