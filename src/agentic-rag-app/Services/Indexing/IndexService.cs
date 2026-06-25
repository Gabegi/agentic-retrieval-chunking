using Azure;
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

    // Creates the index on first run. Skips if it already exists to avoid overwriting portal customisations.
    // To intentionally update the schema, call the dedicated setup-index endpoint.
    public async Task EnsureIndexAsync()
    {
        try
        {
            await _indexClient.GetIndexAsync(_config.SearchIndexName);
            _logger.LogInformation("Index '{Name}' already exists — skipping creation", _config.SearchIndexName);
            return;
        }
        catch (RequestFailedException ex) when (ex.Status == 404) { }

        var index = BuildIndexDefinition(BuildVectorSearch(), BuildSemanticSearch());
        await _indexClient.CreateOrUpdateIndexAsync(index);
        _logger.LogInformation("Index '{Name}' created", _config.SearchIndexName);
    }

    // Configures HNSW vector search with an Azure OpenAI vectorizer for automatic query embedding at search time.
    private VectorSearch BuildVectorSearch()
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
                ModelName      = "text-embedding-3-large"
            }
        });
        return vectorSearch;
    }

    // Configures semantic ranking using content as the primary field and title/heading as keywords.
    private static SemanticSearch BuildSemanticSearch()
    {
        var semanticSearch = new SemanticSearch();
        semanticSearch.Configurations.Add(new SemanticConfiguration("semantic-config", new SemanticPrioritizedFields
        {
            ContentFields  = { new SemanticField("content") },
            KeywordsFields = { new SemanticField("title"), new SemanticField("heading") }
        }));
        semanticSearch.DefaultConfigurationName = "semantic-config";
        return semanticSearch;
    }

    // Assembles the full index schema: fields, vector search config, and semantic search config.
    private SearchIndex BuildIndexDefinition(VectorSearch vectorSearch, SemanticSearch semanticSearch) =>
        new SearchIndex(_config.SearchIndexName)
        {
            Description = "Contains Dutch medical protocols (richtlijnen) with full text content. " +
                          "Use this index to find clinical guidelines, treatment protocols, and medical recommendations " +
                          "for specific conditions or diseases.",
            VectorSearch   = vectorSearch,
            SemanticSearch = semanticSearch,
            Fields =
            {
                new SimpleField("id",               SearchFieldDataType.String) { IsKey = true, IsFilterable = true },
                new SearchableField("title")                                    { IsFilterable = true, IsFacetable = true },
                new SimpleField("source_file",      SearchFieldDataType.String) { IsFilterable = true },
                new SearchableField("content")                                  { AnalyzerName = "nl.microsoft" },
                new SearchableField("heading")                                  { IsFilterable = true, IsFacetable = true, AnalyzerName = "nl.microsoft" },
                new SimpleField("publication_date", SearchFieldDataType.String) { IsFilterable = true },
                new SimpleField("version",          SearchFieldDataType.String) { IsFilterable = true },
                new SimpleField("page_number",      SearchFieldDataType.Int32),
                new SimpleField("chunk_index",      SearchFieldDataType.Int32),
                new VectorSearchField("content_vector", 3072, "vector-profile") { IsHidden = true, IsStored = false }
            }
        };
}
