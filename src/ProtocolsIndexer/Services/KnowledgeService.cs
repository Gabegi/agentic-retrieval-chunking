using Azure.Core;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.KnowledgeBases;
using Azure.Search.Documents.KnowledgeBases.Models;
using InvoiceIndexer.Configuration;
using Microsoft.Extensions.Logging;

namespace InvoiceIndexer.Services;

public class KnowledgeService : IKnowledgeService
{
    private readonly SearchIndexClient _indexClient;
    private readonly IndexerConfig _config;
    private readonly ILogger<KnowledgeService> _logger;


    public KnowledgeService(
        IndexerConfig config,
        TokenCredential credential,
        ILogger<KnowledgeService> logger)
    {
        _indexClient = new SearchIndexClient(new Uri(config.SearchEndpoint), credential);
        _config      = config;
        _logger      = logger;
    }

    public async Task EnsureKnowledgeSourceAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Creating knowledge source '{Name}'", _config.KnowledgeSourceName);

        var knowledgeSource = new SearchIndexKnowledgeSource(
            name: _config.KnowledgeSourceName,
            searchIndexParameters: new SearchIndexKnowledgeSourceParameters(_config.SearchIndexName)
            {
                // Limit BM25 to fields that carry semantic meaning; avoids noise from payment_terms etc.
                SearchFields =
                {
                    new SearchIndexFieldReference("content"),
                    new SearchIndexFieldReference("customer"),
                    new SearchIndexFieldReference("category")
                },
                // All structured fields returned so the model has full invoice context
                SourceDataFields =
                {
                    new SearchIndexFieldReference("id"),
                    new SearchIndexFieldReference("source_file"),
                    new SearchIndexFieldReference("customer"),
                    new SearchIndexFieldReference("category"),
                    new SearchIndexFieldReference("amount"),
                    new SearchIndexFieldReference("discount"),
                    new SearchIndexFieldReference("date"),
                    new SearchIndexFieldReference("order_id"),
                    new SearchIndexFieldReference("ship_mode"),
                    new SearchIndexFieldReference("content")
                }
                // note: content_vector is excluded — it's hidden/not stored and not needed for LLM context
            }
        )
        {
            Description = "Knowledge source for invoice index"
        };

        await _indexClient.CreateOrUpdateKnowledgeSourceAsync(knowledgeSource);

        _logger.LogInformation("Knowledge source '{Name}' created or updated", _config.KnowledgeSourceName);
    }

    public async Task EnsureKnowledgeBaseAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Creating knowledge base '{Name}'", _config.KnowledgeBaseName);

        var aoaiParams = new AzureOpenAIVectorizerParameters
        {
            ResourceUri    = new Uri(_config.OpenAiEndpoint),
            DeploymentName = _config.OpenAiGptDeployment,
            ModelName      = "gpt-4.1"
        };

        var knowledgeBase = new KnowledgeBase(
            name: _config.KnowledgeBaseName,
            knowledgeSources: new[] { new KnowledgeSourceReference(_config.KnowledgeSourceName) }
        )
        {
            // Index description — helps LLM decide whether to query this source
            Description = "Contains SuperStore invoices with customer names, order amounts, " +
                          "discounts, product categories, ship modes and order IDs. " +
                          "Use this index to answer questions about invoice amounts, " +
                          "customer spending, product categories and order history.",

            // Retrieval instructions — guides query planning and source selection
            RetrievalInstructions = "When answering questions about totals or aggregates, " +
                                    "sum the amounts from all matching invoices. " +
                                    "Always cite the order ID and customer name. " +
                                    "For discount questions, look for percentage values. " +
                                    "For category questions, look for product category fields.",

            // Answer instructions — shapes the final response format
            AnswerInstructions = "Provide a concise answer with specific numbers where available. " +
                                 "Always list the relevant invoices with customer name, amount and order ID. " +
                                 "If calculating totals, show the sum clearly.",

            // Answer synthesis — portal returns real answers not raw grounding data
            OutputMode = KnowledgeRetrievalOutputMode.AnswerSynthesis,

            // Medium reasoning effort — deeper subquery generation for aggregate queries
            RetrievalReasoningEffort = new KnowledgeRetrievalMediumReasoningEffort(),

            Models = { new KnowledgeBaseAzureOpenAIModel(aoaiParams) }
        };

        await _indexClient.CreateOrUpdateKnowledgeBaseAsync(knowledgeBase);

        _logger.LogInformation("Knowledge base '{Name}' created or updated", _config.KnowledgeBaseName);
    }
}