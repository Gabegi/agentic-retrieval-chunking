using Azure.Core;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Configuration;

namespace ProtocolsIndexer.Services;

public class IndexService : IIndexService
{
    private readonly SearchIndexClient _indexClient;
    private readonly IndexerConfig _config;
    private readonly ILogger<IndexService> _logger;

    public IndexService(IndexerConfig config, TokenCredential credential, ILogger<IndexService> logger)
    {
        _config      = config;
        _indexClient = new SearchIndexClient(new Uri(config.SearchEndpoint), credential);
        _logger      = logger;
    }

    public async Task EnsureIndexAsync()
    {
        var vectorSearch = new VectorSearch();
        vectorSearch.Algorithms.Add(new HnswAlgorithmConfiguration("hnsw-config"));
        vectorSearch.Profiles.Add(new VectorSearchProfile("vector-profile", "hnsw-config")
        {
            VectorizerName = "openai-vectorizer"
        });
        vectorSearch.Vectorizers.Add(new AzureOpenAIVectorizer("openai-vectorizer")
        {
            Parameters = new AzureOpenAIVectorizerParameters
            {
                ResourceUri    = new Uri(_config.OpenAiEndpoint.TrimEnd('/')),
                DeploymentName = _config.OpenAiEmbeddingDeployment,
                ModelName      = _config.OpenAiEmbeddingDeployment
            }
        });

        var semanticConfig = new SemanticConfiguration("semantic-config", new SemanticPrioritizedFields
        {
            ContentFields  = { new SemanticField("content") },
            KeywordsFields = { new SemanticField("richtlijn_name") }
        });

        var semanticSearch = new SemanticSearch();
        semanticSearch.Configurations.Add(semanticConfig);
        semanticSearch.DefaultConfigurationName = "semantic-config";

        var index = new SearchIndex(_config.SearchIndexName)
        {
            Description = "Contains Dutch medical protocols (richtlijnen) with full text content. " +
                          "Use this index to find clinical guidelines, treatment protocols, and medical recommendations " +
                          "for specific conditions or diseases.",
            VectorSearch   = vectorSearch,
            SemanticSearch = semanticSearch,
            Fields =
            {
                new SimpleField("id",              SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
                new SearchableField("richtlijn_name")                          { IsFilterable = true, IsFacetable = true },
                new SimpleField("source_file",     SearchFieldDataType.String) { IsFilterable = true },
                new SearchableField("content")                                 { AnalyzerName = "nl.microsoft" },
                new VectorSearchField("content_vector", 3072, "vector-profile") { IsHidden = true, IsStored = false }
            }
        };

        await _indexClient.CreateOrUpdateIndexAsync(index);
        _logger.LogInformation("Index '{Name}' created or updated", _config.SearchIndexName);
    }
}
