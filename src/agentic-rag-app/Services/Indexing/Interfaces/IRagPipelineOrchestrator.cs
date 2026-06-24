using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public interface IRagPipelineOrchestrator
{
    Task<IReadOnlyList<ExtractionDocument>> ExtractAsync(string source, CancellationToken ct = default);
    IReadOnlyList<ProtocolDocument> Chunk(IReadOnlyList<ExtractionDocument> docs);
    Task EmbedAndUploadAsync(IReadOnlyList<ProtocolDocument> docs, CancellationToken ct = default);
}
