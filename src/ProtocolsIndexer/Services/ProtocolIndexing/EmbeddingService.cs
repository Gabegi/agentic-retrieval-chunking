using System.ClientModel;
using System.Collections.Concurrent;
using Azure;
using Azure.Core;
using Azure.Search.Documents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Configuration;
using ProtocolsIndexer.Models;
using ProtocolsIndexer.Observability;

namespace ProtocolsIndexer.Services;

public class EmbeddingService : IEmbeddingService
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly SearchClient _searchClient;
    private readonly IRequestTelemetry _telemetry;
    private readonly ILogger<EmbeddingService> _logger;

    public EmbeddingService(
        IndexerConfig config,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        TokenCredential credential,
        IRequestTelemetry telemetry,
        ILogger<EmbeddingService> logger)
    {
        _embeddingGenerator = embeddingGenerator;
        _searchClient       = new SearchClient(new Uri(config.SearchEndpoint), config.SearchIndexName, credential);
        _telemetry          = telemetry;
        _logger             = logger;
    }

    public async Task<IEnumerable<ProtocolDocument>> EmbedDocumentsAsync(
        IEnumerable<ProtocolDocument> documents,
        CancellationToken ct = default)
    {
        var docList = documents.ToList();
        _logger.LogInformation("Embedding {Count} documents", docList.Count);

        var embedded = new ConcurrentBag<ProtocolDocument>();

        await Parallel.ForEachAsync(docList,
            new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct },
            async (document, token) =>
            {
                var text = document.EmbeddingText;
                if (text.Length > 24_000)
                {
                    _logger.LogWarning("Truncating oversized chunk {Id} ({Length} chars)", document.Id, text.Length);
                    text = text[..24_000];
                }

                document.ContentVector = await EmbedWithRetryAsync(text, ct);

                if (document.ContentVector?.Length != 3072)
                    _logger.LogError("Wrong vector dimensions {Dims} for {Id}",
                        document.ContentVector?.Length, document.Id);

                _logger.LogInformation("Embedded {Id} — {Dims} dims", document.Id, document.ContentVector?.Length);
                embedded.Add(document);
            });

        _logger.LogInformation("Embedding complete — {Count}", embedded.Count);
        return embedded;
    }

    private async Task<float[]> EmbedWithRetryAsync(string text, CancellationToken ct)
    {
        const int maxRetries = 5;
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                var result = await _embeddingGenerator.GenerateAsync([text], cancellationToken: ct);
                _telemetry.AddTokens(result.Usage?.InputTokenCount ?? 0, result.Usage?.OutputTokenCount ?? 0);
                return result[0].Vector.ToArray();
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsThrottled(ex))
            {
                if (attempt == maxRetries - 1) throw;
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

    public async Task UploadDocumentsAsync(
        IEnumerable<ProtocolDocument> documents,
        CancellationToken ct = default)
    {
        var docList = documents.ToList();
        _logger.LogInformation("Uploading {Count} documents to index", docList.Count);

        var succeeded = 0;
        var failed    = 0;

        foreach (var batch in docList.Chunk(1000))
        {
            var response = await _searchClient.UploadDocumentsAsync(batch, cancellationToken: ct);

            foreach (var result in response.Value.Results)
            {
                if (!result.Succeeded)
                {
                    _logger.LogWarning("Failed to upload {Key}: {Error}", result.Key, result.ErrorMessage);
                    failed++;
                }
                else
                {
                    succeeded++;
                }
            }
        }

        _logger.LogInformation("Upload complete — {Succeeded} succeeded, {Failed} failed", succeeded, failed);
    }
}
