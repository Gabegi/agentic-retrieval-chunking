using ProtocolsIndexer.Models;
using ProtocolsIndexer.Observability.Reports;

namespace ProtocolsIndexer.Services;

public interface IExtractionService
{
    Task<(IReadOnlyList<ExtractionDocument> Docs, ExtractionResults Stats)> ExtractAsync(
        bool forceReindex, bool overrideMagnitudeCheck = false, CancellationToken ct = default);
}
