namespace AgenticRagApp.Observability.Reports;

// Same shape as CsvIndexRunReport (IndexRunReportBase). Deliberately has no
// StaleDocCount/MissingVersionCount/MissingDepartmentCount - PDF has no equivalent
// concept for any of them (unlike CsvIndexRunReport, which always has real values).
//
// How to use: compare two reports side-by-side after a source change or config tweak
// to see whether quality moved in the right direction.
public sealed record PdfIndexRunReport : IndexRunReportBase
{
    // Quality signal: documents with no zenya_document_id blob metadata set. Non-zero means
    // every citation built from them will show Citation.TraceabilityGap - this is the one
    // metric that tells you, without waiting for a query, how much of the corpus is
    // currently untraceable back to Zenya. Expected to be the full corpus count until
    // whoever uploads PDFs starts setting this metadata.
    public int? TraceabilityGapCount { get; init; }

    // Assembles the report from the three pipeline stage results - any stage can be null
    // (it never ran, e.g. a failure partway through the orchestration), in which case its
    // fields are zeroed rather than left absent.
    public static PdfIndexRunReport FromResults(
        string                 instanceId,
        DateTimeOffset         startedAt,
        DateTimeOffset         finishedAt,
        bool                   forceReindex,
        ExtractionResults?     ext,
        ChunkingResults?       chunk,
        EmbedUploadingResults? embed,
        bool                   success,
        string?                error) => new()
        {
            InstanceId              = instanceId,
            StartedAt               = startedAt,
            FinishedAt              = finishedAt,
            ForceReindex            = forceReindex,
            Success                 = success,
            ErrorMessage            = error,
            DocsToProcess           = ext?.DocsToProcess          ?? 0,
            DocsSkipped             = ext?.DocsSkipped             ?? 0,
            DocsNew                 = ext?.DocsNew                 ?? 0,
            DocsUpdated             = ext?.DocsUpdated             ?? 0,
            DocsDeleted             = ext?.DocsDeleted             ?? 0,
            ChunksRemoved           = embed?.ChunksRemoved         ?? 0,
            ValidationErrors        = ext?.ValidationErrors        ?? 0,
            ValidationWarnings      = ext?.ValidationWarnings      ?? 0,
            ReconciliationProblems  = ext?.ReconciliationProblems  ?? 0,
            MojibakeRepairedPages   = ext?.MojibakeRepairedPages   ?? 0,
            DetectedTableCount      = ext?.DetectedTableCount      ?? 0,
            DocsWithoutHeadings     = ext?.DocsWithoutHeadings     ?? 0,
            MissingTitleCount       = ext?.MissingTitleCount       ?? 0,
            TraceabilityGapCount    = ext?.TraceabilityGapCount    ?? 0,
            ChunksProduced          = chunk?.ChunksProduced        ?? 0,
            DocsWithZeroChunks      = chunk?.DocsWithZeroChunks    ?? 0,
            DuplicateChunks         = chunk?.DuplicateChunks       ?? 0,
            MinChunkSizeChars       = chunk?.MinChunkSizeChars     ?? 0,
            MaxChunkSizeChars       = chunk?.MaxChunkSizeChars     ?? 0,
            AvgChunkSizeChars       = chunk?.AvgChunkSizeChars     ?? 0,
            P95ChunkSizeChars       = chunk?.P95ChunkSizeChars     ?? 0,
            BandUnder100            = chunk?.BandUnder100          ?? 0,
            Band100To500            = chunk?.Band100To500          ?? 0,
            Band500To1500           = chunk?.Band500To1500         ?? 0,
            Band1500Plus            = chunk?.Band1500Plus          ?? 0,
            CoherentChunks          = chunk?.CoherentChunks        ?? 0,
            HeadingsDetected        = chunk?.HeadingsDetected      ?? 0,
            ChunksTruncated         = embed?.ChunksTruncated       ?? 0,
            VectorCacheHits         = embed?.VectorCacheHits       ?? 0,
            EmbeddingRetries        = embed?.EmbeddingRetries      ?? 0,
            VectorDimErrors         = embed?.VectorDimErrors       ?? 0,
            TotalEmbeddingDurationMs = embed?.TotalEmbeddingDurationMs ?? 0,
            DocsUploaded                   = embed?.DocsUploaded                  ?? 0,
            DocsFailed                     = embed?.DocsFailed                    ?? 0,
            IndexDocumentCountSnapshot     = embed?.IndexDocumentCountSnapshot,
            IndexStorageSizeBytesSnapshot  = embed?.IndexStorageSizeBytesSnapshot,
            Issues                  = ext?.Issues         ?? [],
            RedFlags                = [.. ext?.RedFlags ?? [], .. embed?.RedFlags ?? []],
            SpotCheckSample         = ext?.SpotCheckSample ?? [],
        };
}
