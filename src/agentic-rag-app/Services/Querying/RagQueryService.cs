using System.Diagnostics;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using ProtocolsIndexer.Configuration;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

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
        // Step 1: hybrid search — BM25 + vector (search service vectorizes the query via its built-in
        // OpenAI vectorizer), re-ranked with the semantic ranker.
        var opts = new SearchOptions
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

        var searchResponse = await _searchClient.SearchAsync<SearchDocument>(question, opts, ct);
        var chunks = new List<string>();
        await foreach (var result in searchResponse.Value.GetResultsAsync())
        {
            // Title is already prepended to content at index time; use content directly.
            var content = result.Document.GetString("content") ?? "";
            if (!string.IsNullOrWhiteSpace(content))
                chunks.Add(content);
        }
        var retrievedContext = string.Join("\n\n---\n\n", chunks);

        // Step 2: answer synthesis
        var chatClient = _chatClient.GetService<ChatClient>()
            ?? throw new InvalidOperationException(
                "RagQueryService requires an AzureOpenAIClient-backed IChatClient.");

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

        OpenAI.Chat.ChatMessage[] messages =
        [
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(question),
        ];

        var sw         = Stopwatch.StartNew();
        var completion = await chatClient.CompleteChatAsync(messages, cancellationToken: ct);
        sw.Stop();

        return new RagQueryResult(
            Answer:           completion.Value.Content[0].Text,
            RetrievedContext: retrievedContext,
            LatencyMs:        sw.ElapsedMilliseconds,
            InputTokens:      completion.Value.Usage.InputTokenCount,
            OutputTokens:     completion.Value.Usage.OutputTokenCount);
    }
}
