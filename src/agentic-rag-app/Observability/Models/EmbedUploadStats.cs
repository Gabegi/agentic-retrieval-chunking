namespace ProtocolsIndexer.Observability.Reports;

public record EmbedUploadStats(
    int  DocsUploaded,
    int  DocsFailed,
    int  ChunksTruncated,
    int  EmbeddingRetries,
    int  VectorDimErrors,
    long TotalEmbeddingDurationMs,
    long IndexDocumentCount,
    long IndexStorageSizeBytes
);
