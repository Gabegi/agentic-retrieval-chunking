using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public interface IRagQueryService
{
    Task<RagQueryResult> AskAsync(string question, CancellationToken ct = default);
}
