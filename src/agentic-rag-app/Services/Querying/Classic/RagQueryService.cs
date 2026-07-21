using System.Diagnostics;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.AI;
using AgenticRag.Configuration;
using AgenticRag.Models;

namespace AgenticRag.Services;

public class RagQueryService : IRagQueryService
{
    private readonly IChatClient   _chatClient;
    private readonly SearchClient  _searchClient;
    private readonly IndexerConfig _config;

    public RagQueryService(IChatClient chatClient, SearchClient searchClient, IndexerConfig config)
    {
        _chatClient   = chatClient;
        _searchClient = searchClient;
        _config       = config;
    }

    public async Task<RagQueryResult> AskAsync(string question, CancellationToken ct = default)
    {
        var searchOpts = new SearchOptions
        {
            QueryType    = SearchQueryType.Semantic,
            SemanticSearch = new SemanticSearchOptions
            {
                SemanticConfigurationName = "semantic-config",
            },
            VectorSearch = new VectorSearchOptions
            {
                Queries =
                {
                    new VectorizableTextQuery(question)
                    {
                        KNearestNeighborsCount = 10,
                        Fields = { "content_vector" }
                    }
                }
            },
            Select = { "title", "content", "heading", "department", "quick_code" },
            Size   = 10,
        };

        var searchResponse = await _searchClient.SearchAsync<SearchDocument>(question, searchOpts, ct);
        var chunks = new List<string>();
        await foreach (var result in searchResponse.Value.GetResultsAsync())
        {
            var content = result.Document.GetString("content") ?? "";
            if (!string.IsNullOrWhiteSpace(content))
                chunks.Add(content);
        }
        var retrievedContext = string.Join("\n\n---\n\n", chunks);

        var systemPrompt =
            "Je bent een kennisassistent voor Cordaan, een Nederlandse organisatie voor " +
            "ouderen- en gehandicaptenzorg. " +
            "Beantwoord vragen uitsluitend op basis van de aangeleverde documentfragmenten. " +
            "Noem altijd de titel van het document waaruit de informatie afkomstig is. " +
            "Geef volledige en nauwkeurige antwoorden — vat geen stappen, termijnen of " +
            "verantwoordelijkheden samen. " +
            "Als de documenten geen antwoord bevatten, zeg dat dan eerlijk. " +
            "Antwoord in het Nederlands.";

        if (retrievedContext.Length > 0)
            systemPrompt += $"\n\nGeraadpleegde documenten:\n{retrievedContext}";

        var chatMessages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, question),
        };

        var options = new ChatOptions
        {
            Temperature      = 0.0f,
            MaxOutputTokens  = 2000,
            TopP             = null,
            TopK             = null,
            FrequencyPenalty = 0.0f,
            PresencePenalty  = 0.0f,
            Seed             = 42L,
            ResponseFormat   = ChatResponseFormat.Text,
            StopSequences    = null,
            Tools            = null,
            ToolMode         = null,
            ConversationId   = Guid.NewGuid().ToString("N"),
        };

        var endpoint = new Uri(_config.OpenAiEndpoint);
        var sw       = Stopwatch.StartNew();
        var response = await _chatClient.GetResponseAsync(chatMessages, options, ct);
        sw.Stop();

        return new RagQueryResult(
            Answer:            response.Text ?? "",
            RetrievedContext:  retrievedContext,
            SystemInstructions: systemPrompt,
            ChunksRetrieved:   chunks.Count,
            OperationName:     "chat",
            ProviderName:      "openai",
            ServerAddress:     endpoint.Host,
            ServerPort:        endpoint.Port,
            ConversationId:    options.ConversationId!,
            Model:             response.ModelId ?? _config.OpenAiGptDeployment,
            FinishReason:      response.FinishReason?.ToString() ?? "unknown",
            LatencyMs:         sw.ElapsedMilliseconds,
            InputTokens:       response.Usage?.InputTokenCount ?? 0,
            OutputTokens:      response.Usage?.OutputTokenCount ?? 0,
            TotalTokens:       response.Usage?.TotalTokenCount ?? 0,
            Temperature:       options.Temperature,
            MaxOutputTokens:   options.MaxOutputTokens,
            TopP:              options.TopP,
            TopK:              options.TopK,
            FrequencyPenalty:  options.FrequencyPenalty,
            PresencePenalty:   options.PresencePenalty,
            Seed:              options.Seed,
            ResponseFormat:    options.ResponseFormat?.ToString(),
            StopSequences:     options.StopSequences is { Count: > 0 } s ? [.. s] : null,
            Citations:         []);   // parked path — not wired up to build real citations
    }
}
