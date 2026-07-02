using System.Diagnostics;
using Azure.Core;
using Azure.Search.Documents;
using Azure.Search.Documents.KnowledgeBases;
using Azure.Search.Documents.KnowledgeBases.Models;
using ProtocolsIndexer.Configuration;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// Agentic retrieval through the Azure AI Search knowledge base created by
// KnowledgeService. The knowledge base decomposes the question into one or more
// search queries and synthesizes the final answer (AnswerSynthesis), so no
// separate chat completion call is made here. Reference parsing, neighboring-page
// expansion, and token accounting are delegated to KnowledgeBaseReferenceMapper,
// ChunkNeighborExpander, and KnowledgeBaseActivitySummary — this class only
// orchestrates the call and assembles the result.
public class AgenticRagQueryService : IRagQueryService
{
    private readonly KnowledgeBaseRetrievalClient _client;
    private readonly ChunkNeighborExpander        _neighborExpander;
    private readonly IndexerConfig                _config;

    public AgenticRagQueryService(IndexerConfig config, TokenCredential credential, SearchClient searchClient)
    {
        _client = new KnowledgeBaseRetrievalClient(
            new Uri(config.SearchEndpoint), config.KnowledgeBaseName, credential);
        _neighborExpander = new ChunkNeighborExpander(searchClient);
        _config = config;
    }

    public async Task<RagQueryResult> AskAsync(string question, CancellationToken ct = default)
    {
        var request = new KnowledgeBaseRetrievalRequest
        {
            Messages =
            {
                new KnowledgeBaseMessage(new KnowledgeBaseMessageContent[]
                {
                    new KnowledgeBaseMessageTextContent(question),
                })
                { Role = "user" },
            },
            IncludeActivity = true,
        };

        var sw       = Stopwatch.StartNew();
        var response = await _client.RetrieveAsync(request, cancellationToken: ct);
        var result   = response.Value;

        var initialChunks = KnowledgeBaseReferenceMapper.Map(result.References);
        var chunks         = await _neighborExpander.ExpandAsync(initialChunks, ct);
        sw.Stop();

        // One citation per distinct document among the direct hits — neighbor-expansion
        // pages never introduce a new document, only new pages of ones already here.
        var citations = initialChunks
            .GroupBy(c => c.DocumentId)
            .Select(g => new Citation(g.Key, g.First().Title, g.First().QuickCode, g.First().RelativePath))
            .ToList();

        var answer = string.Join("\n", result.Response
            .SelectMany(m => m.Content)
            .OfType<KnowledgeBaseMessageTextContent>()
            .Select(c => c.Text));

        var (inputTokens, outputTokens) = KnowledgeBaseActivitySummary.SumTokens(result.Activity);

        var endpoint = new Uri(_config.SearchEndpoint);
        return new RagQueryResult(
            Answer:             answer,
            RetrievedContext:   string.Join("\n\n---\n\n", chunks),
            SystemInstructions: "knowledge-base retrieval/answer instructions — see KnowledgeService",
            ChunksRetrieved:    chunks.Count,
            OperationName:      "knowledge_base_retrieve",
            ProviderName:       "azure_ai_search",
            ServerAddress:      endpoint.Host,
            ServerPort:         endpoint.Port,
            ConversationId:     Guid.NewGuid().ToString("N"),
            Model:              _config.OpenAiGptModelName,
            FinishReason:       "stop",
            LatencyMs:          sw.ElapsedMilliseconds,
            InputTokens:        inputTokens,
            OutputTokens:       outputTokens,
            TotalTokens:        inputTokens + outputTokens,
            Temperature:        null, MaxOutputTokens: null, TopP: null, TopK: null,
            FrequencyPenalty:   null, PresencePenalty: null, Seed: null,
            ResponseFormat:     null, StopSequences: null,
            Citations:          citations);
    }
}
