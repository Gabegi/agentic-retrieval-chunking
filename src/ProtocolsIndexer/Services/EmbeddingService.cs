using System.ClientModel;
using System.Collections.Concurrent;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Search.Documents;
using Microsoft.Extensions.Logging;
using OpenAI.Embeddings;
using ProtocolsIndexer.Configuration;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public class EmbeddingService : IEmbeddingService
{
    private readonly EmbeddingClient _embeddingClient;
    private readonly SearchClient _searchClient;
    private readonly ILogger<EmbeddingService> _logger;

    public EmbeddingService(
        IndexerConfig config,
        AzureOpenAIClient openAiClient,
        TokenCredential credential,
        ILogger<EmbeddingService> logger)
    {
        _embeddingClient = openAiClient.GetEmbeddingClient(config.OpenAiEmbeddingDeployment);
        _searchClient    = new SearchClient(new Uri(config.SearchEndpoint), config.SearchIndexName, credential);
        _logger          = logger;
    }

    public async Task<IEnumerable<ProtocolDocument>> EmbedDocumentsAsync(
        IEnumerable<ProtocolDocument> documents,
        CancellationToken ct = default)
    {
        var docList = documents.ToList();
        _logger.LogInformation("Embedding {Count} documents", docList.Count);

        var embedded = new ConcurrentBag<ProtocolDocument>();

        await Parallel.ForEachAsync(docList,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (document, token) =>
            {
                var text = document.EmbeddingText;
                if (text.Length > 24_000)
                {
                    _logger.LogWarning("Truncating oversized chunk {Id} ({Length} chars)", document.Id, text.Length);
                    text = text[..24_000];
                }

                document.ContentVector = await EmbedWithRetryAsync(text, token);

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
                var result = await _embeddingClient.GenerateEmbeddingAsync(text, cancellationToken: ct);
                return result.Value.ToFloats().ToArray();
            }
            catch (ClientResultException ex) when (ex.Status == 429)
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
