using ProtocolsIndexer.Models;
using ProtocolsIndexer.Observability.Reports;

namespace ProtocolsIndexer.Services;

public interface IExtractionService
{
    Task<(IReadOnlyList<ExtractionDocument> Docs, ExtractionStats Stats)> ExtractAsync(
        string source, bool forceReindex = false, CancellationToken ct = default);
}
