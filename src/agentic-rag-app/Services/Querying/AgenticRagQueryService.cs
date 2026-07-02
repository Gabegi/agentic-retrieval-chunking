using System.Diagnostics;
using Azure.Core;
using Azure.Search.Documents.KnowledgeBases;
using Azure.Search.Documents.KnowledgeBases.Models;
using ProtocolsIndexer.Configuration;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// Agentic retrieval through the Azure AI Search knowledge base created by
// KnowledgeService. The knowledge base decomposes the question into one or
// more search queries and synthesizes the final answer (AnswerSynthesis),
// so no separate chat completion call is made here.
public class AgenticRagQueryService : IRagQueryService
{
    private readonly KnowledgeBaseRetrievalClient _client;
    private readonly IndexerConfig _config;

    public AgenticRagQueryService(IndexerConfig config, TokenCredential credential)
    {
        _client = new KnowledgeBaseRetrievalClient(
            new Uri(config.SearchEndpoint), config.KnowledgeBaseName, credential);
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
        sw.Stop();
        var result = response.Value;

        var answer = string.Join("\n", result.Response
            .SelectMany(m => m.Content)
            .OfType<KnowledgeBaseMessageTextContent>()
            .Select(c => c.Text));

        // References carry the grounding chunks (the SourceDataFields configured in
        // KnowledgeService) — exactly what Groundedness/Retrieval evaluators need.
        // SourceData values are BinaryData (raw JSON) — deserialize rather than
        // ToString(), which would leave string fields JSON-quoted/escaped.
        var chunks = result.References
            .Select(r =>
            {
                if (r.SourceData is null || !r.SourceData.TryGetValue("content", out var contentRaw))
                    return null;
                var content = AsText(contentRaw);
                r.SourceData.TryGetValue("title", out var titleRaw);
                var title = AsText(titleRaw);
                return title is null ? content : $"[{title}]\n{content}";
            })
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!)
            .ToList();

        // Query-planning + answer-synthesis token usage lives in per-step activity
        // records rather than a rolled-up total, so sum them ourselves.
        var inputTokens = 0L;
        var outputTokens = 0L;
        if (result.Activity is not null)
        {
            foreach (var activity in result.Activity)
            {
                switch (activity)
                {
                    case KnowledgeBaseModelQueryPlanningActivityRecord planning:
                        inputTokens += planning.InputTokens ?? 0;
                        outputTokens += planning.OutputTokens ?? 0;
                        break;
                    case KnowledgeBaseModelAnswerSynthesisActivityRecord synthesis:
                        inputTokens += synthesis.InputTokens ?? 0;
                        outputTokens += synthesis.OutputTokens ?? 0;
                        break;
                }
            }
        }

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
            ResponseFormat:     null, StopSequences: null);
    }

    private static string? AsText(object? value) => value switch
    {
        null => null,
        BinaryData bd => bd.ToObjectFromJson<string>(),
        _ => value.ToString(),
    };
}
