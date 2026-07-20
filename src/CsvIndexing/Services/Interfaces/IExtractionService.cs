using IndexingShared.Models;
using IndexingShared.Observability.Reports;

namespace CsvIndexing.Services;

public interface IExtractionService
{
    Task<(IReadOnlyList<ExtractionDocument> Docs, ExtractionResults Stats)> ExtractAsync(
        bool forceReindex, bool overrideMagnitudeCheck = false, CancellationToken ct = default);
}
