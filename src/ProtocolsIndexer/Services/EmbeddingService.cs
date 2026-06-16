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
            new ParallelOptions { MaxDegreeOfParallelism = 10, CancellationToken = ct },
            async (document, token) =>
            {
                var result = await _embeddingClient.GenerateEmbeddingAsync(
                    document.EmbeddingText, cancellationToken: token);

                document.ContentVector = result.Value.ToFloats().ToArray();

                if (document.ContentVector?.Length != 3072)
                    _logger.LogError("Wrong vector dimensions {Dims} for {Id}",
                        document.ContentVector?.Length, document.Id);

                _logger.LogInformation("Embedded {Id} — {Dims} dims", document.Id, document.ContentVector?.Length);
                embedded.Add(document);
            });

        _logger.LogInformation("Embedding complete — {Count}", embedded.Count);
        return embedded;
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
