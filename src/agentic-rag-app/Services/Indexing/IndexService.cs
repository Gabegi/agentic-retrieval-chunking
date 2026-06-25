using Azure;
using Azure.Core;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Configuration;

namespace ProtocolsIndexer.Services;

// Manages the Azure AI Search index lifecycle: creates the index on first run (skips if already present),
// and defines the full schema — fields, HNSW vector search, and semantic ranking configuration.
// To force a schema update on an existing index, call the dedicated setup-index HTTP endpoint.
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

    // Configures semantic ranking: title in TitleField (not KeywordsFields), content as primary, heading as keyword.
    private static SemanticSearch BuildSemanticSearch()
    {
        var semanticSearch = new SemanticSearch();
        semanticSearch.Configurations.Add(new SemanticConfiguration("semantic-config", new SemanticPrioritizedFields
        {
            TitleField     = new SemanticField("title"),
            ContentFields  = { new SemanticField("content") },
            KeywordsFields = { new SemanticField("heading") }
        }));
        semanticSearch.DefaultConfigurationName = "semantic-config";
        return semanticSearch;
    }

    // Assembles the full index schema: fields, vector search config, and semantic search config.
    private SearchIndex BuildIndexDefinition(VectorSearch vectorSearch, SemanticSearch semanticSearch) =>
        new SearchIndex(_config.SearchIndexName)
        {
            Description = "Internal knowledge base for Cordaan (Dutch elderly and disability care " +
              "organization). Contains the full text of organizational documents: care and " +
              "quality protocols, work instructions, job descriptions (functiebeschrijvingen), " +
              "HR policies, facility and safety plans, financial procedures, privacy/security " +
              "policies, and software manuals (e.g. ONS/ECD, CIS). Use this index for questions " +
              "about Cordaan's internal policies, procedures, role responsibilities, and " +
              "care-related instructions.",
            VectorSearch   = vectorSearch,
            SemanticSearch = semanticSearch,
            Fields =
            {
                // NOTE: populated as a composite key at write time, e.g. {documentId}_p{pageNumber}_c{chunkIndex}.
                // DOCUMENT_ID alone is not unique per chunk — using it raw would make later chunks overwrite earlier ones.
                new SimpleField("id",                 SearchFieldDataType.String)         { IsKey = true, IsFilterable = true },
                // Raw DOCUMENT_ID (one value shared by all chunks of the same document).
                // Used by IndexDocumentService to query and batch-delete all chunks for a given document.
                new SimpleField("document_id",        SearchFieldDataType.String)         { IsFilterable = true },
                new SearchableField("title")                                               { IsFilterable = true, IsFacetable = true },
                new SearchableField("content")                                             { AnalyzerName = "nl.microsoft" },
                new SearchableField("heading")                                             { IsFilterable = true, IsFacetable = true, AnalyzerName = "nl.microsoft" },
                // From FOLDER_MINI_FULL_PATH — bounded set of department/category values (HR, Kwaliteit, Facilitaire zaken, ...).
                new SearchableField("department")                                          { IsFilterable = true, IsFacetable = true },
                // From QUICK_CODE — Cordaan's internal document code; useful for exact-match lookups and citing source docs.
                new SimpleField("quick_code",         SearchFieldDataType.String)         { IsFilterable = true },
                // From LAST_MODIFIED_DATETIME. Renamed from publication_date (no true pub date exists) and retyped to enable date filtering/sorting.
                new SimpleField("last_modified_date", SearchFieldDataType.DateTimeOffset) { IsFilterable = true, IsSortable = true },
                // From CHECK_DATE — the next review/expiry date. Lets the RAG layer flag or exclude stale protocols.
                new SimpleField("check_date",         SearchFieldDataType.DateTimeOffset) { IsFilterable = true, IsSortable = true },
                // Populate as VERSION.REVISION (e.g. "7.0") — REVISION was previously dropped entirely.
                new SimpleField("version",            SearchFieldDataType.String)         { IsFilterable = true },
                new SimpleField("page_number",        SearchFieldDataType.Int32),
                new SimpleField("chunk_index",        SearchFieldDataType.Int32),
                new VectorSearchField("content_vector", 3072, "vector-profile")           { IsHidden = true, IsStored = false }
            }
        };
}
