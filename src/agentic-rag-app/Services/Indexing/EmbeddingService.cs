using System.ClientModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using Azure;
using Azure.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Models;
using ProtocolsIndexer.Observability;

namespace ProtocolsIndexer.Services;

public class EmbeddingService : IEmbeddingService
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly ILogger<EmbeddingService> _logger;

    public EmbeddingService(
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        ILogger<EmbeddingService> logger)
    {
        _embeddingGenerator = embeddingGenerator;
        _logger             = logger;
    }

    public async Task<EmbeddingRunResult> EmbedDocumentsAsync(
        IEnumerable<ProtocolDocument> documents,
        CancellationToken ct = default)
    {
        var docList = documents.ToList();
        _logger.LogInformation("Embedding {Count} documents", docList.Count);

        var embedded         = new ConcurrentBag<ProtocolDocument>();
        var chunksTruncated  = 0;
        var embeddingRetries = 0;
        var vectorDimErrors  = 0;
        var totalDurationMs  = 0L;

        await Parallel.ForEachAsync(docList,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (document, token) =>
            {
                var text = document.EmbeddingText;
                if (text.Length > 24_000)
                {
                    _logger.LogWarning("Truncating oversized chunk {Id} ({Length} chars)", document.Id, text.Length);
                    text = text[..24_000];
                    Interlocked.Increment(ref chunksTruncated);
                    Instrumentation.ChunksTruncated.Add(1);
                }

                var sw = Stopwatch.StartNew();
                var (vector, retries) = await EmbedWithRetryAsync(text, token);
                sw.Stop();

                var elapsedMs = sw.ElapsedMilliseconds;
                Interlocked.Add(ref totalDurationMs, elapsedMs);
                Interlocked.Add(ref embeddingRetries, retries);
                Instrumentation.EmbeddingDurationMs.Record(elapsedMs);

                document.ContentVector = vector;

                if (document.ContentVector?.Length != 3072)
                {
                    _logger.LogError("Wrong vector dimensions {Dims} for {Id}",
                        document.ContentVector?.Length, document.Id);
                    Interlocked.Increment(ref vectorDimErrors);
                    Instrumentation.VectorDimErrors.Add(1);
                }

                _logger.LogInformation("Embedded {Id} — {Dims} dims", document.Id, document.ContentVector?.Length);
                embedded.Add(document);
            });

        _logger.LogInformation("Embedding complete — {Count}", embedded.Count);

        return new EmbeddingRunResult(
            Documents:       embedded,
            ChunksTruncated: chunksTruncated,
            EmbeddingRetries: embeddingRetries,
            VectorDimErrors: vectorDimErrors,
            TotalDurationMs: totalDurationMs);
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
        ClientResultException      cre => cre.Status == 429,
        Azure.RequestFailedException rfe => rfe.Status == 429,
        _ => false
    };
}
