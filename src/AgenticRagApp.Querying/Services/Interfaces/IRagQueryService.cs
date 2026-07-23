using AgenticRagApp.Querying.Models;

namespace AgenticRagApp.Querying.Services;

public interface IRagQueryService
{
    Task<RagQueryResult> AskAsync(string question, CancellationToken ct = default);
}
