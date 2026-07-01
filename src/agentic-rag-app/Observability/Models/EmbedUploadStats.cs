namespace ProtocolsIndexer.Observability.Reports;

public record EmbedUploadStats(
    int   DocsUploaded,
    int   DocsFailed,
    int   ChunksTruncated,
    int   EmbeddingRetries,
    int   VectorDimErrors,
    long  TotalEmbeddingDurationMs,
    // Snapshot taken after upload. Azure Search stats lag live writes by minutes —
    // use this for corpus drift checks, not for "did this run add N chunks" (use DocsUploaded for that).
    long? IndexDocumentCountSnapshot,
    long? IndexStorageSizeBytesSnapshot
);
