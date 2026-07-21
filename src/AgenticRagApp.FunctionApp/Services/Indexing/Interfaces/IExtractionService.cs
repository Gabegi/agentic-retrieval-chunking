using AgenticRagApp.Models;
using AgenticRagApp.Observability.Reports;

namespace AgenticRagApp.Services;

public interface IExtractionService
{
    Task<(IReadOnlyList<ExtractionDocument> Docs, ExtractionResults Stats)> ExtractAsync(
        bool forceReindex, CancellationToken ct = default);
}
