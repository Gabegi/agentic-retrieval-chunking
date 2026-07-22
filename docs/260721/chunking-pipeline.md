# Chunking — what it does, step by step

This covers the second of the three indexing steps (`ExtractActivity` →
**`ChunkActivity`** → `EmbedAndUploadActivity`) — turning extracted
documents into the smaller text chunks that actually get embedded and
searched. See `docs/extraction-pipeline.md` for what happens before this
step, and how a document ends up here in the first place.

## Where it runs

`ChunkingService.ChunkDocuments()` (`Services/Indexing/ChunkingService.cs`),
called from `IndexingFunction.ChunkActivity`. Input: the documents written
to blob by the extract step. Output: a list of `ProtocolDocument` chunks,
written to blob for the embed/upload step to pick up.

## Why chunk at all

A document (e.g. one Zenya protocol) can be several thousand characters
long. Azure AI Search — and the embedding model — both work best on
smaller, focused pieces of text: a chunk that's too large dilutes the
embedding (it stops being "about" any one thing precisely) and returns too
much irrelevant text alongside the relevant part. So each document is split
into chunks before it's embedded and indexed.

## The steps, per document

### 1. Pick the strategy

Chunking is behind an interface (`IChunkingStrategy`) so different chunking
approaches could be swapped in later. Today there's exactly one
implementation registered: `ChunkingStrategy1` ("Sentence-Aware Sliding
Window").

### 2. Split the content (`ChunkingStrategy1.cs`)

- If the whole page's content already fits under `maxChars` (default
  **1,500** characters), it becomes a single chunk — no further splitting.
- Otherwise, a window of `maxChars` slides forward through the text:
  1. **Prefer a sentence boundary** — look backward from the end of the
     window for a `.`, `!`, or `?` followed by a space, so the chunk ends on
     a complete thought rather than mid-sentence.
  2. **Fall back to a word boundary** if no sentence end is found in the
     back half of the window.
  3. **Hard-split** at exactly `maxChars` if neither is found at all (rare —
     e.g. one very long run-on sentence).
- After each chunk is cut, the next chunk doesn't start exactly where the
  last one ended — it steps back **~150 characters** (`overlapChars`) and
  starts from the nearest preceding sentence instead. This means a
  fact sitting right at a chunk boundary appears in *both* the chunk before
  and the chunk after it, so a search query that lands near a boundary
  doesn't miss it.
- This repeats until the remaining text is shorter than `maxChars`, which
  becomes the final chunk.

### 3. Assemble each chunk (`ChunkingService.cs`)

Each raw text piece from step 2 becomes a `ProtocolDocument` — the actual
record that gets embedded and stored in the search index:

- **Id** — deterministic, built from `{DocumentId}::{PageOrdinal}::{ChunkIndexWithinPage}`.
  Same document + same page + same chunk position always produces the same
  id. This matters a lot for updates: re-uploading a chunk with an
  unchanged id overwrites it in place; a chunk whose id no longer appears
  in a new run is what makes it detectable as stale (see "What happens to
  old chunks" below).
- **Content** — the document's **title** is prepended to every chunk (so
  even a short continuation chunk with no obvious keywords of its own still
  carries its parent document's identity for search matching), followed by
  the chunk's own heading (if any) and text.
- **Summary** — kept as its own separate field rather than folded into
  Content, since the same summary would otherwise repeat on every chunk of
  a multi-chunk document.
- Metadata carried over from extraction as-is: department/folder,
  quick code, relative path, last-modified date, check date, version,
  page number, chunk index.

### 4. Stats and telemetry

After all documents are chunked, `ChunkingResults.Compute()` builds a
summary (chunk count, size distribution, duplicate chunks, how many chunks
have a heading, etc.) that ends up in the run report, plus per-chunk-size
histograms sent to Application Insights.

## What happens to old chunks

Chunking itself doesn't touch the live search index — it only produces the
new chunk list in memory/blob. The actual index cleanup (deleting chunks
that no longer exist after a document was updated or removed) happens
later, in the upload step, **after** the new chunks from this step are
successfully embedded and uploaded — not before. See the "Diff against the
live index" and cleanup sections in `docs/extraction-pipeline.md` for why
that ordering matters.
