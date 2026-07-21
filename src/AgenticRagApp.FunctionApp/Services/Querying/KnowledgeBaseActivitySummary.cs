using Azure.Search.Documents.KnowledgeBases.Models;

namespace AgenticRagApp.Services;

// Query-planning + answer-synthesis token usage lives in per-step activity records
// rather than a rolled-up total on the response, so sum them here.
public static class KnowledgeBaseActivitySummary
{
    public static (long InputTokens, long OutputTokens) SumTokens(IEnumerable<KnowledgeBaseActivityRecord>? activity)
    {
        long input = 0, output = 0;
        if (activity is null)
            return (input, output);

        foreach (var record in activity)
        {
            switch (record)
            {
                case KnowledgeBaseModelQueryPlanningActivityRecord planning:
                    input  += planning.InputTokens ?? 0;
                    output += planning.OutputTokens ?? 0;
                    break;
                case KnowledgeBaseModelAnswerSynthesisActivityRecord synthesis:
                    input  += synthesis.InputTokens ?? 0;
                    output += synthesis.OutputTokens ?? 0;
                    break;
            }
        }
        return (input, output);
    }
}
