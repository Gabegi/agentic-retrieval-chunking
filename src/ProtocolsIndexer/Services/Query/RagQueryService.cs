using System.Diagnostics;
using Azure.AI.OpenAI.Chat;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using ProtocolsIndexer.Configuration;

namespace ProtocolsIndexer.Services;

public record RagQueryResult(
    string Answer,
    string RetrievedContext,
    long   LatencyMs,
    int    InputTokens,
    int    OutputTokens);

public interface IRagQueryService
{
    Task<RagQueryResult> AskAsync(string question, CancellationToken ct = default);
}

public class RagQueryService : IRagQueryService
{
    private readonly AzureOpenAIClient _openAi;
    private readonly IndexerConfig     _config;

    public RagQueryService(AzureOpenAIClient openAi, IndexerConfig config)
    {
        _openAi = openAi;
        _config = config;
    }

    public async Task<RagQueryResult> AskAsync(string question, CancellationToken ct = default)
    {
        var chatClient = _openAi.GetChatClient(_config.OpenAiGptDeployment);

#pragma warning disable AOAI001
        var options = new ChatCompletionOptions();
        options.AddDataSource(new AzureSearchChatDataSource
        {
            Endpoint              = new Uri(_config.SearchEndpoint),
            IndexName             = _config.SearchIndexName,
            Authentication        = DataSourceAuthentication.FromSystemManagedIdentity(),
            QueryType             = DataSourceQueryType.VectorSemanticHybrid,
            SemanticConfiguration = "semantic-config",
            VectorizationSource   = DataSourceVectorizer.FromDeploymentName(_config.OpenAiEmbeddingDeployment),
            FieldMappings = new DataSourceFieldMappings
            {
                ContentFieldNames = { "content" },
                TitleFieldName    = "richtlijn_name",
                FilePathFieldName = "source_file",
                VectorFieldNames  = { "content_vector" },
            },
        });
#pragma warning restore AOAI001

        ChatMessage[] messages =
        [
            new SystemChatMessage(
                "Je bent een medische informatieassistent voor LCI-richtlijnen (Landelijke Coördinatie Infectieziektebestrijding). " +
                "Beantwoord vragen uitsluitend op basis van de aangeleverde richtlijnen. " +
                "Noem altijd de naam van de richtlijn en de sectie waaruit de informatie afkomstig is. " +
                "Geef volledige en nauwkeurige antwoorden — vat geen doseringen, termijnen of diagnostische criteria samen. " +
                "Als meerdere richtlijnen relevant zijn, bespreek elke richtlijn afzonderlijk. " +
                "Antwoord in het Nederlands."),
            new UserChatMessage(question),
        ];

        var sw         = Stopwatch.StartNew();
        var completion = await chatClient.CompleteChatAsync(messages, options, ct);
        sw.Stop();

#pragma warning disable AOAI001
        var citations        = completion.Value.GetMessageContext()?.Citations ?? [];
#pragma warning restore AOAI001
        var retrievedContext = string.Join("\n\n---\n\n",
            citations.Select(c => string.IsNullOrEmpty(c.Title) ? c.Content : $"[{c.Title}]\n{c.Content}"));

        return new RagQueryResult(
            Answer:           completion.Value.Content[0].Text,
            RetrievedContext: retrievedContext,
            LatencyMs:        sw.ElapsedMilliseconds,
            InputTokens:      completion.Value.Usage.InputTokenCount,
            OutputTokens:     completion.Value.Usage.OutputTokenCount);
    }
}
