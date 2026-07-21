namespace AgenticRagApp.Observability.Reports;

// Written to blob after every development CSV indexing run.
// Path: pipeline-reports/indexing/{yyyy}/{MM}/{dd}/{instanceId}.json
//
// Same shape as PdfIndexRunReport plus three CSV-only fields (StaleDocCount,
// MissingVersionCount, MissingDepartmentCount) that always have real values for CSV,
// unlike PDF where they have no equivalent concept at all. Never mix PDF and CSV data
// in one report — each pipeline writes its own report type to its own path.
public record CsvIndexRunReport(
    // ── Identity ──────────────────────────────────────────────────────────────
    string         InstanceId,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    bool           ForceReindex,
    bool           Success,
    string?        ErrorMessage,

    // ── Extraction ────────────────────────────────────────────────────────────

    int DocsToProcess,
    int DocsSkipped,
    int DocsNew,
    int DocsUpdated,
    int DocsDeleted,
    int ChunksRemoved,

    int ValidationErrors,
    int ValidationWarnings,
    int ReconciliationProblems,

    // Quality signal: docs past their check_date — live but potentially stale guidance in
    // the index. Retrieval will surface it as if it were current — flag to content owners.
    int StaleDocCount,

    int MojibakeRepairedPages,
    int DetectedTableCount,
    int DocsWithoutHeadings,
    int MissingTitleCount,
    int MissingVersionCount,
    int MissingDepartmentCount,

    // ── Chunking ─────────────────────────────────────────────────────────────

    int ChunksProduced,
    int DocsWithZeroChunks,
    int DuplicateChunks,
    long   MinChunkSizeChars,
    long   MaxChunkSizeChars,
    double AvgChunkSizeChars,
    long   P95ChunkSizeChars,
    int    BandUnder100,
    int    Band100To500,
    int    Band500To1500,
    int    Band1500Plus,
    int    CoherentChunks,
    int    HeadingsDetected,

    // ── Embedding ────────────────────────────────────────────────────────────

    int  ChunksTruncated,
    int  VectorCacheHits,
    int  EmbeddingRetries,
    int  VectorDimErrors,
    long TotalEmbeddingDurationMs,

    // ── Upload ────────────────────────────────────────────────────────────────

    int DocsUploaded,
    int DocsFailed,
    long? IndexDocumentCountSnapshot,
    long? IndexStorageSizeBytesSnapshot,

    // ── Detail (development only) ─────────────────────────────────────────────

    IReadOnlyList<ValidationIssueEntry> Issues,
    IReadOnlyList<string>               RedFlags,
    IReadOnlyList<SpotCheckEntry>       SpotCheckSample
);
