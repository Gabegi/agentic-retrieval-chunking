using System.ClientModel;
using Azure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Models;
using ProtocolsIndexer.Observability;

namespace ProtocolsIndexer.Services;

public class EmbeddingService : IEmbeddingService
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly ILogger<EmbeddingService>                     _logger;

    private const int MaxParallelism = 4;
    private const int TruncationLimit = 24_000;
    private const int ExpectedDimensions = 3072;

    public EmbeddingService(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        ILogger<EmbeddingService>                     logger)
    {
        _embeddingGenerator = embeddingGenerator;
        _logger             = logger;
    }

    public async Task<EmbeddingRunResult> EmbedDocumentsAsync(
        IEnumerable<ProtocolDocument> documents,
        CancellationToken ct = default)
    {
        var docList   = documents.ToList();
        var semaphore = new SemaphoreSlim(MaxParallelism);

        _logger.LogInformation("Embedding {Count} documents", docList.Count);

        var tasks   = docList.Select(doc => EmbedOneAsync(doc, semaphore, ct)).ToList();
        var results = await Task.WhenAll(tasks);

        _logger.LogInformation("Embedding complete — {Count} embedded", results.Length);

        return new EmbeddingRunResult(
            Documents:        results.Select(r => r.Document),
            ChunksTruncated:  results.Count(r => r.Truncated),
            EmbeddingRetries: results.Sum(r => r.Retries),
            VectorDimErrors:  results.Count(r => r.DimError));
    }

    private async Task<EmbedChunkResult> EmbedOneAsync(
        ProtocolDocument doc, SemaphoreSlim semaphore, CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            var text      = doc.EmbeddingText;
            var truncated = text.Length > TruncationLimit;

            if (truncated)
            {
                _logger.LogWarning("Truncating oversized chunk {Id} ({Length} chars)", doc.Id, text.Length);
                text = text[..TruncationLimit];
                Instrumentation.ChunksTruncated.Add(1);
            }

            var (vector, retries) = await EmbedWithRetryAsync(text, ct);
            doc.ContentVector     = vector;

            var dimError = doc.ContentVector?.Length != ExpectedDimensions;
            if (dimError)
            {
                _logger.LogError("Wrong vector dimensions {Dims} for {Id}", doc.ContentVector?.Length, doc.Id);
                Instrumentation.VectorDimErrors.Add(1);
            }

            return new EmbedChunkResult(doc, truncated, dimError, retries);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<(float[] Vector, int Retries)> EmbedWithRetryAsync(string text, CancellationToken ct)
    {
        const int maxRetries = 5;
        var retries = 0;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                var result = await _embeddingGenerator.GenerateAsync([text], cancellationToken: ct);
                return (result[0].Vector.ToArray(), retries);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsThrottled(ex))
            {
                if (attempt == maxRetries - 1) throw;
                retries++;
                Instrumentation.EmbeddingRetries.Add(1);
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)); // 2s, 4s, 8s, 16s
                _logger.LogWarning("OpenAI throttled (429), retry {Attempt}/{Max} in {Delay}s",
                    attempt + 1, maxRetries, delay.TotalSeconds);
                await Task.Delay(delay, ct);
            }
        }

        throw new InvalidOperationException("Unreachable");
    }

    private static bool IsThrottled(Exception ex) => ex switch
    {
        ClientResultException        cre => cre.Status == 429,
        RequestFailedException       rfe => rfe.Status == 429,
        _ => false
    };

    private record EmbedChunkResult(ProtocolDocument Document, bool Truncated, bool DimError, int Retries);
}
