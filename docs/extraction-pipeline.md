# CSV extraction pipeline — what it does, step by step

This explains the extraction path of the indexing Function App
(`src/agentic-rag-app`), from the raw Zenya export to documents sitting in
Azure AI Search. It's the first of the three Durable Functions activities
that make up a full indexing run (`ExtractActivity` → `ChunkActivity` →
`EmbedAndUploadActivity`) — this doc only covers `ExtractActivity`, since
that's where the CSV-specific logic (and the validation gate) lives.

Exactly one `IExtractionOrchestrator` is registered at a time
(`CsvExtractionOrchestrator` today, in `program.cs`) — the pipeline doesn't
branch on source at request time. Switching to a different source (e.g. a
future PDF extractor) means swapping that one DI registration, not passing
a parameter.

## How to trigger a run

```
POST /api/index
POST /api/index?force=true                    # reindex every doc, even unchanged ones
POST /api/index?overrideMagnitudeCheck=true    # see "If a run gets blocked" below
```

This starts a Durable Functions orchestration and returns an instance ID
you can poll for status (standard Durable Functions "check status"
response).

## The steps

### 1. Download

Two files are pulled from blob storage: `zenya_pages.csv` (page content) and
`zenya_index.csv` (per-document metadata — title, version, check date,
attention flags). These are dropped into the container by an external Zenya
export process outside this repo.

### 2. Parse (`CsvExtractor.cs`)

Each CSV is read row by row into typed records (`PageRecord`,
`IndexRecord`). A handful of columns are required (e.g. `DOCUMENT_ID`,
`PAGE_INDEX`, `PAGE_CONTENT` for pages) — if the header is missing one, the
whole file is rejected immediately with one clear error instead of failing
row-by-row thousands of times. Individual bad rows (bad `PAGE_INDEX`, empty
`DOCUMENT_ID`, etc.) are recorded as row-level errors and skipped rather
than aborting the file, unless the parser hits 25 unreadable rows in a row —
that's treated as "this isn't actually a CSV" and aborts.

### 3. Join (`CsvJoiner.cs`)

Pages are matched to their index record by `DOCUMENT_ID`. A page whose
document is marked inactive in the index is skipped (with a warning); a
page with no matching index record at all is an error. An index record with
no matching pages is tracked separately (`SkippedIndexRecords`) — it'll
never make it into the search index, since there's no content to index.

### 4. Clean (`DataCleaner.cs`)

Joined records are normalized: HTML entities decoded, the "Cordaan" logo
boilerplate line stripped, image placeholders removed, excess blank lines
collapsed, dates parsed (`LAST_MODIFIED_DATETIME`, `CHECK_DATE`),
`ATTENTION_REQUIRED_FLAGS` parsed as JSON. A page that becomes blank after
cleanup still gets a warning — it usually means the source page had nothing
but boilerplate/an image.

### 5. Validate (`PipelineValidator.cs`) — the safety gate

This is the step that decides whether the run is trustworthy enough to
touch the live search index. It checks:

- **Error rate** — errors from every step above, as a percentage of every
  row attempted. Must be ≤ 1%.
- **Reconciliation** — record counts must add up exactly at each step
  boundary (parse → join → clean). A mismatch means something got silently
  dropped or duplicated somewhere upstream of what's being counted, which
  the checks above wouldn't otherwise catch.
- **Magnitude shift** — this run's cleaned record count vs. the last
  *successful* run's count. A swing of more than 20% either way is treated
  as suspicious — it usually means the export itself is broken (truncated,
  wrong file, an upstream filter changed), not that the source data
  genuinely changed that much overnight.

**All three must pass, or the whole run aborts before anything is written
to the search index.** This matters because of what step 6 does next.

Two things that are *reported* but don't block the run: text-quality
warnings (mojibake, wrong language tag, uneven markdown tables) and domain
red flags (documents overdue for review, documents with no markdown
headings). These get logged and included in the run report, but they're
informational — worth a look, not worth aborting over.

### 6. Diff against the live index (`ExtractionService.cs`)

Only reached if validation passed. Every document in this run is compared
against what's currently indexed (by last-modified date) and classified:

- **New** — not indexed yet → goes on to chunking/embedding.
- **Updated** — modified since it was last indexed → flagged as *stale*
  (see below) and goes on to be re-chunked/re-embedded.
- **Unchanged** — skipped, unless `force=true` was passed.
- **Removed** — was indexed before, isn't in this run's output at all →
  flagged as *stale*, with no replacement content coming.

This is exactly why step 5's validation matters: "removed" is *inferred*
from absence. If the export were broken and only returned half the real
documents, every document in the missing half would look "removed" — which
is precisely the failure mode the magnitude-shift check exists to catch.

**Nothing is deleted from the search index at this point.** This step only
produces a list of *stale document ids* (updated + removed) and passes it
forward to the chunk/embed/upload steps. See "Stale-chunk cleanup" below for
why the deletion itself is deferred to the very end of the pipeline, and
`docs/chunking-pipeline.md` for what happens to these documents in the
meantime.

## Stale-chunk cleanup — deferred, not immediate

Deleting a document's old chunks *before* its replacement chunks are ready
would leave a real gap: if extraction deleted eagerly and a later step
(chunking, embedding, upload) then failed, that document would have **zero**
chunks in the index — not stale, just gone — until the next successful run.

To avoid that, cleanup happens last, in `UploadService.UploadDocumentsAsync`,
only after the new chunks for this run have already been uploaded
successfully:

1. Every chunk id currently in the index for a stale document id is fetched.
2. Any id that's part of what was *just* uploaded this run is excluded —
   re-chunking a document whose content didn't shift enough to change its
   chunk count typically produces the exact same chunk ids (see
   `docs/chunking-pipeline.md`), so those get overwritten in place and were
   never actually stale.
3. Whatever remains — chunk ids from before that don't correspond to any
   chunk this run produced — is genuinely orphaned and gets deleted.

A **removed** document has no new chunks at all, so every one of its old
chunk ids ends up in that "remains" set and gets deleted, same as before.
An **updated** document only loses the specific old chunk ids that its new
version no longer produces (e.g. a page that used to split into 3 chunks
now fits in 2) — the rest were already overwritten in step 2.

If chunking, embedding, or upload fails partway through, this cleanup step
is never reached — the old chunks stay exactly as they were, stale but
present, until the next successful run retries the same document.

## If a run gets blocked

A blocked run doesn't update its own baseline (the "previous successful
count" used for the magnitude check is only saved after a passing run), so
just retrying the same data will fail again with the same numbers — nothing
changes on its own.

If you've checked the logs (the aborted run logs exactly which check failed
and by how much) and you're confident the shift is real — a genuinely large
batch of new protocols, or a deliberate bulk removal — re-trigger with:

```
POST /api/index?overrideMagnitudeCheck=true
```

This bypasses **only** the magnitude-shift check. It does **not** bypass
the error-rate or reconciliation checks — those mean the data itself is
malformed, and no query parameter should be able to wave that through. When
used, the run logs a loud, distinctly-tagged warning
(`VALIDATION OVERRIDE APPLIED`) so it shows up clearly in App Insights and
isn't mistaken for a normal pass.

After an override run completes, its record count becomes the new baseline
— the **next** run goes back to being checked normally against that new
number. The override is a one-time unblock, not a standing exemption.
