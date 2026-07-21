using Azure;
using Azure.Storage.Blobs;

namespace AgenticRagApp.Infrastructure.Clients.Blob;

// Generic wrapper around BlobContainerClient — every blob read/write in the app goes
// through this, regardless of which container or which pipeline. Callers decide which
// container to pass and how to orchestrate calls (parallel, sequential, one file vs many);
// this only ever does the mechanical Azure call.
public interface IBlobStore
{
    Task EnsureContainerExistsAsync(BlobContainerClient container, CancellationToken ct = default);

    Task<byte[]> DownloadBytesAsync(BlobContainerClient container, string blobName, CancellationToken ct = default);

    Task<Stream> OpenReadAsync(BlobContainerClient container, string blobName, CancellationToken ct = default);

    Task<bool> ExistsAsync(BlobContainerClient container, string blobName, CancellationToken ct = default);

    Task UploadAsync(BlobContainerClient container, string blobName, BinaryData content, bool overwrite, CancellationToken ct = default);

    Task<bool> DeleteIfExistsAsync(BlobContainerClient container, string blobName, CancellationToken ct = default);

    // Cheap listing — blob name, storage LastModified, and content length only, no content download.
    Task<IReadOnlyList<(string Name, DateTimeOffset? LastModified, long? ContentLength)>> ListBlobsAsync(BlobContainerClient container, CancellationToken ct = default);

    Task<T> DownloadJsonAsync<T>(BlobContainerClient container, string blobName, CancellationToken ct = default);

    Task UploadJsonAsync<T>(BlobContainerClient container, string blobName, T value, CancellationToken ct = default);

    // Returns (default, null) if the blob doesn't exist yet — "no previous baseline" is a
    // normal, expected outcome for state blobs, not an error.
    Task<(T? Value, ETag? ETag)> TryReadJsonWithETagAsync<T>(BlobContainerClient container, string blobName, CancellationToken ct = default);

    // Optimistic-concurrency write: matches previousETag if given, otherwise requires the
    // blob not exist yet (first write wins). Returns false (and logs a warning) instead of
    // throwing if another writer won the race — losing this race isn't worth failing an
    // otherwise-successful caller over.
    Task<bool> SaveJsonWithETagAsync<T>(BlobContainerClient container, string blobName, T value, ETag? previousETag, CancellationToken ct = default);
}
