using ProtocolsIndexer.Models;
using ProtocolsIndexer.Observability.Reports;

namespace ProtocolsIndexer.Services;

public interface IExtractionService
{
    Task<(IReadOnlyList<ExtractionDocument> Docs, ExtractionResults Stats)> ExtractAsync(
        CancellationToken ct = default);
}
