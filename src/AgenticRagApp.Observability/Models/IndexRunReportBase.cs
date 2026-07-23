namespace AgenticRagApp.Observability.Reports;

// Written to blob after every development indexing run.
// Path: pipeline-reports/indexing/{yyyy}/{MM}/{dd}/{instanceId}.json
//
// Shared shape for both index run reports - CsvIndexRunReport adds StaleDocCount/
// MissingVersionCount/MissingDepartmentCount (real values, always present for CSV);
// PdfIndexRunReport adds TraceabilityGapCount (PDF's own metric, no CSV equivalent).
// Never mix PDF and CSV data in one report — each pipeline writes its own report type
// to its own path.
public abstract record IndexRunReportBase
{
    // ── Identity ──────────────────────────────────────────────────────────────
    public required string         InstanceId   { get; init; } // Durable orchestration ID — correlate with App Insights traces
    public required DateTimeOffset StartedAt    { get; init; }
    public required DateTimeOffset FinishedAt   { get; init; }
    public required bool           ForceReindex { get; init; } // true = all docs re-indexed regardless of last-modified date
    public required bool           Success      { get; init; }
    public string?                 ErrorMessage { get; init; }

    // ── Extraction ────────────────────────────────────────────────────────────

    // Quality signal: DocsNew + DocsUpdated is your actual workload for this run.
    // A high DocsSkipped ratio means the corpus is stable — incremental indexing is working.
    public int DocsToProcess { get; init; } // docs queued for chunking + embedding (= DocsNew + DocsUpdated)
    public int DocsSkipped   { get; init; } // unchanged docs skipped — content identical to last indexed version
    public int DocsNew       { get; init; } // first-time docs never in the index before
    public int DocsUpdated   { get; init; } // docs whose source changed — stale chunks deleted, then re-indexed
    public int DocsDeleted   { get; init; } // stale chunk batches deleted for changed docs (before re-insert)

    // Quality signal: the actual chunk rows removed for changed docs — one doc can own many chunks,
    // so this is the real cost of DocsUpdated. Net this against ChunksProduced/DocsUploaded to see
    // true corpus growth for the run, since DocsUploaded alone doesn't account for what was deleted.
    public int ChunksRemoved { get; init; }

    // Quality signal: ValidationErrors > 0 means corrupted or malformed source data made it
    // into the pipeline. Check the Issues list below for the specific records.
    // ValidationWarnings are soft (mojibake, inconsistent tables) — worth reviewing but not blocking.
    public int ValidationErrors   { get; init; }
    public int ValidationWarnings { get; init; }
    // Quality signal: should always be 0. Non-zero means row counts don't add up across pipeline
    // stages, which indicates a logic bug or data truncation.
    public int ReconciliationProblems { get; init; }

    // Quality signal: pages where known mojibake patterns were auto-repaired. Non-zero is
    // expected on Dutch text with accented characters — watch for a run-over-run jump, not
    // the absolute count, since a jump means an upstream encoding problem got worse.
    public int MojibakeRepairedPages { get; init; }

    // Quality signal: total markdown table blocks detected this run. No ground truth for
    // "expected" count yet — watch for a drop vs. recent runs; a table flattened into prose
    // surfaces here as a dip.
    public int DetectedTableCount { get; init; }

    // Quality signal: docs with no markdown headings get chunked with no structural guidance.
    // These chunks may be lower quality. Check whether the source has headings at all,
    // or whether the extraction step failed to preserve them.
    public int DocsWithoutHeadings { get; init; }

    // Quality signal: MissingTitle is the most damaging. The title is prepended to every chunk
    // and is the primary BM25 signal in retrieval. Zero is ideal; investigate any non-zero count.
    public int MissingTitleCount { get; init; }

    // ── Chunking ─────────────────────────────────────────────────────────────

    public int ChunksProduced { get; init; }

    // Quality signal: non-zero means extracted content was empty or whitespace-only after cleaning.
    // Those docs are in the pipeline but contribute nothing to the index.
    public int DocsWithZeroChunks { get; init; }

    // Quality signal: identical content indexed more than once wastes vector space and causes
    // duplicate results in retrieval. Non-zero usually means duplicate pages in the source.
    public int DuplicateChunks { get; init; }

    // Quality signal: the chunk size distribution tells you whether the chunking strategy is working.
    public long   MinChunkSizeChars { get; init; }
    public long   MaxChunkSizeChars { get; init; }
    public double AvgChunkSizeChars { get; init; }
    public long   P95ChunkSizeChars { get; init; }
    public int    BandUnder100      { get; init; }
    public int    Band100To500      { get; init; }
    public int    Band500To1500     { get; init; }
    public int    Band1500Plus      { get; init; }

    // Quality signal: CoherentChunks start with a capital letter or digit and end with punctuation —
    // a proxy for well-formed sentence boundaries. Low ratio means the chunker is cutting mid-sentence.
    // HeadingsDetected: chunks with a heading set benefit from structural context in retrieval.
    public int CoherentChunks   { get; init; }
    public int HeadingsDetected { get; init; }

    // ── Embedding ────────────────────────────────────────────────────────────

    // Quality signal: truncated chunks were embedded with incomplete content.
    // The vector represents only the first 24k chars — retrieval quality for those chunks is degraded.
    public int ChunksTruncated { get; init; }
    // Quality/cost signal: chunks whose vector was reused from the cache instead of
    // re-embedded. High relative to ChunksProduced on an updated-document run means the
    // hash-based dedup is doing its job.
    public int VectorCacheHits { get; init; }
    // A spike here means you hit OpenAI rate limits — consider reducing parallelism or adding quota.
    public int EmbeddingRetries { get; init; }
    // Should always be 0. Non-zero means the embedding model returned wrong dimensions — a config mismatch.
    public int  VectorDimErrors           { get; init; }
    public long TotalEmbeddingDurationMs  { get; init; }

    // ── Upload ────────────────────────────────────────────────────────────────

    // Quality signal: DocsFailed > 0 means those chunks are silently missing from the index.
    // Retrieval will not find content from failed chunks.
    public int DocsUploaded { get; init; }
    public int DocsFailed   { get; init; }

    // Quality signal: track IndexDocumentCountSnapshot across runs for corpus drift checks.
    // Null if the stats API call failed (run results are unaffected).
    public long? IndexDocumentCountSnapshot      { get; init; }
    public long? IndexStorageSizeBytesSnapshot   { get; init; }

    // ── Detail (development only) ─────────────────────────────────────────────

    // Full issue list with document IDs — use to identify which source records to investigate.
    public required IReadOnlyList<ValidationIssueEntry> Issues { get; init; }
    // High-priority signals: docs without headings, magnitude shifts vs previous run.
    public required IReadOnlyList<string> RedFlags { get; init; }
    // Random sample of cleaned records for manual inspection — spot-check content fidelity.
    public required IReadOnlyList<SpotCheckEntry> SpotCheckSample { get; init; }
}
