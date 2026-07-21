using Azure.Search.Documents.Indexes.Models;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure.Clients.Search;
using AgenticRagApp.Infrastructure.Configuration;

namespace AgenticRagApp.Services;

// Manages the Azure AI Search index lifecycle: creates the index on first run (skips if
// already present), and defines the full schema — fields, HNSW vector search, and
// semantic ranking configuration.
//
// EnsureIndexAsync only ever creates a *missing* index - it never updates an existing
// one, specifically to avoid a code-driven push silently overwriting any portal-side
// customisation nobody told this class about. There's no schema-update path today (no
// "setup-index" endpoint actually exists despite what an earlier version of this comment
// claimed) - adding one is only worth doing once there's a real index to update against.
public class IndexService : IIndexService
{
    private readonly ISearchIndexStore     _indexStore;
    private readonly IndexerConfig         _config;
    private readonly ILogger<IndexService> _logger;

    public IndexService(IndexerConfig config, ISearchIndexStore indexStore, ILogger<IndexService> logger)
    {
        _config     = config;
        _indexStore = indexStore;
        _logger     = logger;
    }

    // Creates the index on first run. Skips if it already exists - see the class comment
    // above for why.
    public async Task EnsureIndexAsync()
    {
        var index   = BuildIndexDefinition(BuildVectorSearch(), BuildSemanticSearch());
        var created = await _indexStore.EnsureIndexAsync(index);
        _logger.LogInformation(created ? "Index '{Name}' created" : "Index '{Name}' already exists — skipping creation", _config.SearchIndexName);
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
                // Breadcrumb (from the PDF's bookmark outline) or DI-detected heading for
                // this page - see ChunkingService. Was always null before the chunking
                // rewrite (docs/chunking-rewrite-plan.md), when nothing ever set it.
                new SearchableField("heading")                                             { IsFilterable = true, IsFacetable = true, AnalyzerName = "nl.microsoft" },
                // The blob's own storage LastModified.
                new SimpleField("last_modified_date", SearchFieldDataType.DateTimeOffset) { IsFilterable = true, IsSortable = true },
                new SimpleField("page_number",        SearchFieldDataType.Int32),
                new SimpleField("chunk_index",        SearchFieldDataType.Int32),

                // Document Intelligence structural signals (docs/chunking-rewrite-plan.md's
                // Tier 2) - derived on DocumentChunk from the raw Tables/Figures/
                // AverageWordConfidence fields already carried through from extraction.
                new SimpleField("table_count",        SearchFieldDataType.Int32)          { IsFilterable = true },
                new SimpleField("has_table",          SearchFieldDataType.Boolean)        { IsFilterable = true, IsFacetable = true },
                new SimpleField("page_quality",       SearchFieldDataType.Double)         { IsFilterable = true, IsSortable = true },
                new SearchableField("figure_captions", collection: true)                   { AnalyzerName = "nl.microsoft" },

                new VectorSearchField("content_vector", _config.OpenAiEmbeddingDimensions, "vector-profile") { IsHidden = true, IsStored = false }
            }
        };

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
                ModelName      = _config.OpenAiEmbeddingModelName
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
}
