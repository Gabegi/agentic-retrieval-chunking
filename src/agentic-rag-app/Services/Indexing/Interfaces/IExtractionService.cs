using AgenticRag.Models;
using AgenticRag.Observability.Reports;

namespace AgenticRag.Services;

public interface IExtractionService
{
    Task<(IReadOnlyList<ExtractionDocument> Docs, ExtractionResults Stats)> ExtractAsync(
        bool forceReindex, CancellationToken ct = default);
}
