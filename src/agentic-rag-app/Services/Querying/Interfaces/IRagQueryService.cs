using AgenticRag.Models;

namespace AgenticRag.Services;

public interface IRagQueryService
{
    Task<RagQueryResult> AskAsync(string question, CancellationToken ct = default);
}
