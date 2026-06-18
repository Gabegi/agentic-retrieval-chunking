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
    private readonly SearchIndexClient       _indexClient;
    private readonly IndexerConfig           _config;
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
                SearchFields =
                {
                    new SearchIndexFieldReference("content"),
                    new SearchIndexFieldReference("heading"),
                    new SearchIndexFieldReference("richtlijn_name"),
                    new SearchIndexFieldReference("content_vector")
                },
                SourceDataFields =
                {
                    new SearchIndexFieldReference("id"),
                    new SearchIndexFieldReference("source_file"),
                    new SearchIndexFieldReference("richtlijn_name"),
                    new SearchIndexFieldReference("heading"),
                    new SearchIndexFieldReference("page_number"),
                    new SearchIndexFieldReference("content")
                }
            }
        )
        {
            Description = "Knowledge source for Dutch medical protocols index"
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
            Description = "Contains Dutch medical protocols (richtlijnen) covering clinical guidelines, " +
                          "treatment protocols, and medical recommendations for a wide range of conditions.",

            RetrievalInstructions = "Search for protocols by condition name, treatment type, or specialty. " +
                                    "The content is in Dutch — use Dutch medical terminology when searching. " +
                                    "Always cite the richtlijn_name and source_file in your answer.",

            AnswerInstructions = "Provide a comprehensive and complete answer based on the protocol content. " +
                                 "Do not summarize or shorten clinical steps, dosing regimens, incubation periods, or diagnostic criteria. " +
                                 "Always mention which richtlijn (guideline) and section the information comes from. " +
                                 "If multiple protocols are relevant, discuss each separately.",

            OutputMode               = KnowledgeRetrievalOutputMode.AnswerSynthesis,
            RetrievalReasoningEffort = new KnowledgeRetrievalHighReasoningEffort(),
            Models                   = { new KnowledgeBaseAzureOpenAIModel(aoaiParams) }
        };

        await _indexClient.CreateOrUpdateKnowledgeBaseAsync(knowledgeBase, onlyIfUnchanged: false, ct);
        _logger.LogInformation("Knowledge base '{Name}' created or updated", _config.KnowledgeBaseName);
    }
}
