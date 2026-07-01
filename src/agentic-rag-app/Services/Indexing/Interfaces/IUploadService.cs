using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public interface IUploadService
{
    Task<UploadResult> UploadDocumentsAsync(IEnumerable<ProtocolDocument> documents, CancellationToken ct = default);
}

public record UploadResult(
    int   DocsUploaded,
    int   DocsFailed,
    long? IndexDocumentCountSnapshot,
    long? IndexStorageSizeBytesSnapshot
);
