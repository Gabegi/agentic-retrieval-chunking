using IndexingShared.Models;

namespace IndexingShared.Observability.Reports;

// Written to blob after every development indexing run.
// Path: pipeline-reports/indexing/{yyyy}/{MM}/{dd}/{instanceId}.json
//
// How to use: compare two reports side-by-side after a source change or config tweak
// to see whether quality moved in the right direction. Fields marked "Quality signal"
// are the ones most likely to explain a retrieval regression.
public record IndexRunReport(
    // ── Identity ──────────────────────────────────────────────────────────────
    string         InstanceId,    // Durable orchestration ID — correlate with App Insights traces
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string         Source,        // which extractor ran (e.g. "csv")
    bool           ForceReindex,  // true = all docs re-indexed regardless of last-modified date
    bool           Success,
    string?        ErrorMessage,

    // ── Extraction ────────────────────────────────────────────────────────────

    // Quality signal: DocsNew + DocsUpdated is your actual workload for this run.
    // A high DocsSkipped ratio means the corpus is stable — incremental indexing is working.
    int DocsToProcess,   // docs queued for chunking + embedding (= DocsNew + DocsUpdated)
    int DocsSkipped,     // unchanged docs skipped — content identical to last indexed version
    int DocsNew,         // first-time docs never in the index before
    int DocsUpdated,     // docs whose source changed — stale chunks deleted, then re-indexed
    int DocsDeleted,     // stale chunk batches deleted for changed docs (before re-insert)

    // Quality signal: the actual chunk rows removed for changed docs — one doc can own many chunks,
    // so this is the real cost of DocsUpdated. Net this against ChunksProduced/DocsUploaded to see
    // true corpus growth for the run, since DocsUploaded alone doesn't account for what was deleted.
    int ChunksRemoved,

    // Quality signal: ValidationErrors > 0 means corrupted or malformed source data made it
    // into the pipeline. Check the Issues list below for the specific records.
    // ValidationWarnings are soft (mojibake, inconsistent tables) — worth reviewing but not blocking.
    int ValidationErrors,
    int ValidationWarnings,
    // Quality signal: should always be 0. Non-zero means row counts don't add up across pipeline
    // stages (Parse → Join → Clean), which indicates a logic bug or data truncation.
    int ReconciliationProblems,

    // Quality signal: StaleDocCount > 0 means live guidance past its review date is in the index.
    // Retrieval will surface it as if it were current — flag to content owners. Null (not 0)
    // means the source has no equivalent attention-flag concept (e.g. PDF).
    int? StaleDocCount,

    // Quality signal: pages where known mojibake patterns were auto-repaired. Non-zero is
    // expected on Dutch text with accented characters — watch for a run-over-run jump, not
    // the absolute count, since a jump means an upstream encoding problem got worse.
    int MojibakeRepairedPages,

    // Quality signal: total markdown table blocks detected this run. No ground truth for
    // "expected" count yet — watch for a drop vs. recent runs; a table flattened into prose
    // surfaces here as a dip.
    int DetectedTableCount,

    // Quality signal: docs with no markdown headings get chunked with no structural guidance.
    // These chunks may be lower quality. Check whether the source has headings at all,
    // or whether the extraction step failed to preserve them.
    int DocsWithoutHeadings,

    // Quality signal: MissingTitle is the most damaging. The title is prepended to every chunk
    // and is the primary BM25 signal in retrieval. Zero is ideal; investigate any non-zero count.
    int MissingTitleCount,
    // Null (not 0) means the source has no equivalent concept (e.g. no Version/department
    // data for PDFs) — distinct from "verified zero missing."
    int? MissingVersionCount,
    int? MissingDepartmentCount,

    // ── Chunking ─────────────────────────────────────────────────────────────

    int ChunksProduced,

    // Quality signal: non-zero means extracted content was empty or whitespace-only after cleaning.
    // Those docs are in the pipeline but contribute nothing to the index.
    int DocsWithZeroChunks,

    // Quality signal: identical content indexed more than once wastes vector space and causes
    // duplicate results in retrieval. Non-zero usually means duplicate pages in the source.
    int DuplicateChunks,

    // Quality signal: the chunk size distribution tells you whether the chunking strategy is working.
    // For the SentenceAwareSlidingWindow (max 1500 chars, overlap 150):
    //   BandUnder100  — near-empty chunks; bad vectors; investigate source content
    //   Band100To500  — short but usable; may lack context for complex queries
    //   Band500To1500 — target zone; most chunks should land here
    //   Band1500Plus  — at or over the strategy max; risk of embedding truncation
    // p95 near 1500 is healthy. p95 >> 1500 means truncation risk at embedding time.
    long   MinChunkSizeChars,
    long   MaxChunkSizeChars,
    double AvgChunkSizeChars,
    long   P95ChunkSizeChars,
    int    BandUnder100,
    int    Band100To500,
    int    Band500To1500,
    int    Band1500Plus,

    // Quality signal: CoherentChunks start with a capital letter or digit and end with punctuation —
    // a proxy for well-formed sentence boundaries. Low ratio means the chunker is cutting mid-sentence.
    // HeadingsDetected: chunks with a heading set benefit from structural context in retrieval.
    int CoherentChunks,
    int HeadingsDetected,

    // ── Embedding ────────────────────────────────────────────────────────────

    // Quality signal: truncated chunks were embedded with incomplete content.
    // The vector represents only the first 24k chars — retrieval quality for those chunks is degraded.
    int  ChunksTruncated,
    // A spike here means you hit OpenAI rate limits — consider reducing parallelism or adding quota.
    int  EmbeddingRetries,
    // Should always be 0. Non-zero means the embedding model returned wrong dimensions — a config mismatch.
    int  VectorDimErrors,
    long TotalEmbeddingDurationMs,

    // ── Upload ────────────────────────────────────────────────────────────────

    // Quality signal: DocsFailed > 0 means those chunks are silently missing from the index.
    // Retrieval will not find content from failed chunks.
    int DocsUploaded,
    int DocsFailed,

    // Quality signal: track IndexDocumentCountSnapshot across runs for corpus drift checks.
    // Null if the stats API call failed (run results are unaffected).
    // Never diff this run-over-run for "did this run add N chunks" — use DocsUploaded for that.
    // Azure Search stats lag live writes by minutes, so this reflects the state shortly after upload.
    long? IndexDocumentCountSnapshot,
    long? IndexStorageSizeBytesSnapshot,

    // ── Detail (development only) ─────────────────────────────────────────────

    // Full issue list with document IDs — use to identify which source records to investigate.
    IReadOnlyList<ValidationIssueEntry> Issues,
    // High-priority signals: stale docs, docs without headings, magnitude shifts vs previous run.
    IReadOnlyList<string>               RedFlags,
    // Random sample of 5 cleaned records for manual inspection — spot-check content fidelity.
    IReadOnlyList<SpotCheckEntry>       SpotCheckSample
);
