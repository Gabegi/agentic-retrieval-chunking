using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public interface IIndexingPipelineOrchestrator
{
    Task<IReadOnlyList<ExtractionDocument>> ExtractAsync(string source, bool forceReindex = false, CancellationToken ct = default);
    IReadOnlyList<ProtocolDocument> Chunk(IReadOnlyList<ExtractionDocument> docs);
    Task EmbedAndUploadAsync(IReadOnlyList<ProtocolDocument> docs, CancellationToken ct = default);
}
