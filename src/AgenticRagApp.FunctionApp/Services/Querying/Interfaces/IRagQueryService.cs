using AgenticRagApp.Models;

namespace AgenticRagApp.Services;

public interface IRagQueryService
{
    Task<RagQueryResult> AskAsync(string question, CancellationToken ct = default);
}
