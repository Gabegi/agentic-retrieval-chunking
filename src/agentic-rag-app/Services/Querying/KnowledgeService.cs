using Azure.Core;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.KnowledgeBases;
using Azure.Search.Documents.KnowledgeBases.Models;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Configuration;

namespace ProtocolsIndexer.Services;

public class KnowledgeService : IKnowledgeService
{
    private readonly SearchIndexClient        _indexClient;
    private readonly IndexerConfig            _config;
    private readonly ILogger<KnowledgeService> _logger;

    public KnowledgeService(
        IndexerConfig              config,
        TokenCredential            credential,
        ILogger<KnowledgeService>  logger)
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
                // Limit BM25 to fields that carry semantic meaning
                SearchFields =
                {
                    new SearchIndexFieldReference("content"),
                    new SearchIndexFieldReference("title"),
                    new SearchIndexFieldReference("heading"),
                    new SearchIndexFieldReference("department"),
                },
                // All structured fields returned so the model has full document context
                SourceDataFields =
                {
                    new SearchIndexFieldReference("id"),
                    new SearchIndexFieldReference("document_id"),
                    new SearchIndexFieldReference("title"),
                    new SearchIndexFieldReference("heading"),
                    new SearchIndexFieldReference("department"),
                    new SearchIndexFieldReference("quick_code"),
                    new SearchIndexFieldReference("version"),
                    new SearchIndexFieldReference("content"),
                }
                // note: content_vector is excluded — not needed for LLM context
            }
        )
        {
            Description = "Knowledge source for Zenya corporate document index"
        };

        await _indexClient.CreateOrUpdateKnowledgeSourceAsync(knowledgeSource, onlyIfUnchanged: false, ct);
        _logger.LogInformation("Knowledge source '{Name}' created or updated", _config.KnowledgeSourceName);
    }

    public async Task EnsureKnowledgeBaseAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Creating knowledge base '{Name}'", _config.KnowledgeBaseName);

        var aoaiParams = new AzureOpenAIVectorizerParameters
        {
            ResourceUri    = new Uri(_config.OpenAiEndpoint),
            DeploymentName = _config.OpenAiGptDeployment,
            ModelName      = _config.OpenAiGptModelName
        };

        var knowledgeBase = new KnowledgeBase(
            name: _config.KnowledgeBaseName,
            knowledgeSources: new[] { new KnowledgeSourceReference(_config.KnowledgeSourceName) }
        )
        {
            Description = "Contains Zenya corporate documents including procedures, guidelines, " +
                          "and policies. Use this index to answer questions about document content, " +
                          "processes, responsibilities, and organizational procedures.",

            RetrievalInstructions = "Search for documents by title, topic, or document type. " +
                                    "Always cite the document title and source file in your answer. " +
                                    "For process or procedure questions, look for the relevant procedure document. " +
                                    "If multiple documents are relevant, discuss each separately.",

            AnswerInstructions = "Provide a complete and accurate answer based on the document content. " +
                                 "Always mention which document the information comes from. " +
                                 "Do not summarize or omit steps from procedures or guidelines. " +
                                 "If multiple documents are relevant, discuss each one separately.",

            OutputMode               = KnowledgeRetrievalOutputMode.AnswerSynthesis,
            RetrievalReasoningEffort = new KnowledgeRetrievalMediumReasoningEffort(),
            Models                   = { new KnowledgeBaseAzureOpenAIModel(aoaiParams) }
        };

        await _indexClient.CreateOrUpdateKnowledgeBaseAsync(knowledgeBase, onlyIfUnchanged: false, ct);
        _logger.LogInformation("Knowledge base '{Name}' created or updated", _config.KnowledgeBaseName);
    }
}
