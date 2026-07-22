namespace AgenticRagApp.Infrastructure.Clients.Embedding;

// Generic wrapper around IEmbeddingGenerator — one embedding API call with retry/backoff
// on throttling and transient failures. Batching, truncation, per-item logging, dimension
// checks, and caching decisions are all caller-specific and stay in the Indexing projects.
public interface IEmbeddingClient
{
    Task<(float[][] Vectors, int Retries)> EmbedWithRetryAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}
