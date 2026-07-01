using ProtocolsIndexer.Models;
using ProtocolsIndexer.Observability.Reports;

namespace ProtocolsIndexer.Services;

public interface IIndexingPipelineOrchestrator
{
    Task<(IReadOnlyList<ExtractionDocument> Docs, ExtractionStats Stats)> ExtractAsync(
        string source, bool forceReindex = false, CancellationToken ct = default);

    (IReadOnlyList<ProtocolDocument> Docs, ChunkStats Stats) Chunk(IReadOnlyList<ExtractionDocument> docs);

    Task<EmbedUploadStats> EmbedAndUploadAsync(IReadOnlyList<ProtocolDocument> docs, CancellationToken ct = default);
}
