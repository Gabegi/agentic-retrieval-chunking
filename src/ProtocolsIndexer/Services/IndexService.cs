using Azure.Core;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using InvoiceIndexer.Configuration;
using Microsoft.Extensions.Logging;

namespace InvoiceIndexer.Services;

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
            // Semantic keywords — minor boost to ranking for key fields
            KeywordsFields =
            {
                new SemanticField("customer"),
                new SemanticField("category"),
                new SemanticField("order_id")
            }
        });

        var semanticSearch = new SemanticSearch();
        semanticSearch.Configurations.Add(semanticConfig);
        semanticSearch.DefaultConfigurationName = "semantic-config";

        var index = new SearchIndex(_config.SearchIndexName)
        {
            // Index description — helps LLM decide whether to query this index
            Description = "Contains SuperStore invoices with customer names, order amounts, " +
                          "discounts, product categories, ship modes and order IDs. " +
                          "Use this index to answer questions about invoice amounts, " +
                          "customer spending, product categories and order history.",
            VectorSearch   = vectorSearch,
            SemanticSearch = semanticSearch,
            Fields =
            {
                new SimpleField("id",       SearchFieldDataType.String)           { IsKey = true, IsFilterable = true },
                new SearchableField("customer")                                   { IsFilterable = true, IsFacetable = true },
                new SimpleField("amount",   SearchFieldDataType.Double)           { IsFilterable = true, IsSortable = true },
                new SimpleField("discount", SearchFieldDataType.Double)           { IsFilterable = true, IsSortable = true },
                new SearchableField("category")                                   { IsFilterable = true, IsFacetable = true },
                new SimpleField("date",     SearchFieldDataType.DateTimeOffset)   { IsFilterable = true, IsSortable = true },
                new SimpleField("order_id",    SearchFieldDataType.String)         { IsFilterable = true },
                new SearchableField("ship_mode")                                  { IsFilterable = true, IsFacetable = true },
                new SimpleField("source_file", SearchFieldDataType.String)        { IsFilterable = true },
                new SearchableField("content")                                    { AnalyzerName = "en.microsoft" },
                new VectorSearchField("content_vector", 3072, "vector-profile")  { IsHidden = true, IsStored = false }
            }
        };

        await _indexClient.CreateOrUpdateIndexAsync(index);

        _logger.LogInformation("Index '{Name}' created or updated", _config.SearchIndexName);
    }
}