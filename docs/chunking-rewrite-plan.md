# ChunkingService rewrite: use what PDF extraction actually produces

Plan written 2026-07-21, following a full audit of `PDFExtractionResult` and
what data from it actually reaches `ChunkingService` today. Scope is
chunking/metadata only — the chunk-*splitting* strategy (`ChunkingStrategy1`/
`ChunkingStrategy2`) is explicitly out of scope, to be reviewed separately.

## Why: `ChunkingService` is still shaped for the old CSV/Zenya source

`ChunkingService` reads six metadata keys (`folder_path`, `quick_code`,
`relative_path`, `check_date`, `version`, `summary`) that Zenya's CSV export
had and PDF extraction never produces. `PdfExtractionOrchestrator`'s
`ExtractionDocument.Metadata` only ever sets `title` and `last_modified_date`
— confirmed by reading the code, not inferred. So for every PDF chunk ever
produced: `ProtocolDocument.Department`, `.QuickCode`, `.RelativePath`,
`.CheckDate`, `.Version`, and `.Summary` are always `null`. `Summary` in
particular also drives a dedicated Search field (searchable + part of the
semantic config) and a fold-in branch in `ProtocolDocument.EmbeddingText` —
both permanently no-ops for PDF.

Separately, and worse: `TextChunk.Heading` defaults to `null`, and **neither**
`ChunkingStrategy1` nor `ChunkingStrategy2` ever sets it — both only ever
construct `new TextChunk(index, content)`. So `chunk.Heading != null` in
`ChunkingService` is always false, `ProtocolDocument.Heading` is always
`null`, the Search index's `heading` field (searchable, filterable,
facetable, part of `KeywordsFields` in semantic config) is permanently
empty, and `ChunkingResults.HeadingsDetected` — documented in
`IndexRunReport` as a quality signal — is permanently `0`.

## What extraction actually produces vs. what reaches `ChunkingService`

| Data (on `PDFExtractionResult`) | Reaches `ExtractionDocument` today? | What it's worth |
|---|---|---|
| `Title` (native/filename) | ✅ yes | already works |
| blob `LastModified` | ✅ yes (via blob property, Stage 1) | already works |
| `NativeMetadata.Author` / `CreatedAt` | ❌ no | low-value byline/date, cheap to add |
| `SectionBreadcrumbs` (page → `"Chapter 3 > 3.2 Dosage"`, from bookmarks) | ❌ no — computed by `PDFSectionBreadCrumbBuilder`, then discarded | **highest value**: real content for the currently-dead `Heading` field |
| `Structure.Headings` (DI-detected, per page, works even with no bookmarks) | ❌ no — only used inside `PdfPipelineValidator`'s own checks | fallback heading source when a doc has no bookmark outline |
| `Structure.Tables` (per page) | ❌ no — validation-only today | table-count/has-table signal per chunk |
| `Structure.PageQuality` (OCR word confidence, per page) | ❌ no | real quality signal — flag likely-garbled OCR pages, currently just thrown away |
| `Structure.Figures` (captions, per page) | ❌ no | figure captions, useful for retrieval |
| `Structure.PageDimensions` / `Lines` / `SelectionMarks` | n/a | correctly out of scope — exist for a future highlight-on-source UI feature, not retrieval |
| `Structure.Sections` (DI's real semantic boundaries) | n/a | belongs to chunk-*boundary* strategy — deferred, reviewed separately |
| `Structure.Boilerplate` (header/footer/footnote roles) | ❌ no | adjacent to *cleaning*, not chunking — flagged, not part of this plan |

## Root cause: one missing parameter

`PdfCleaner.CleanPdf` only ever took plain page text (`IReadOnlyList<PdfPageRecord>`)
— it was never going to carry `Structure`. The actual break is one level up:
`PdfExtractionOrchestrator.BuildExtractionOutput(report, cleanResult, errors,
warnings, missingTitle, lastModifiedByBlob)` builds every `ExtractionDocument`,
but never receives `fileResults` — the `List<PDFExtractionResult>` that
actually holds `Structure`/`SectionBreadcrumbs`/`NativeMetadata`. `fileResults`
exists one call frame up, in `ExtractDocumentsAsync`, and today gets used only
by the validator, then dropped.

Also confirmed: `ChunkingService` is the **only** consumer of
`ExtractionDocument.Metadata` anywhere in the codebase — full latitude to
reshape it, nothing else depends on its current shape.

## Tier 1 — no Search index schema change, ships immediately

Adding a *new* schema field requires a schema-update path that doesn't fully
exist yet — `IndexService.EnsureIndexAsync` only creates a missing index, it
never migrates an existing one (the comment referencing a "dedicated
setup-index endpoint" points at something that isn't actually implemented).
So anything that stays inside *existing* fields ships now; anything new is
Tier 2.

1. ✅ `PdfExtractionOrchestrator.ExtractDocumentsAsync` → pass `fileResults`
   into `BuildExtractionOutput`.
2. ✅ `BuildExtractionOutput`: two lookups built from `fileResults` —
   `BuildNativeMetadataLookup` (Author/CreatedAt, per blob) and
   `BuildPageContextLookup` (Breadcrumb from the bookmark outline via
   `SectionBreadcrumbs`, Heading from `Structure.Headings` filtered to
   title/sectionHeading roles per page — sparse by design, only pages with
   one or the other get an entry). Joined into `ExtractionDocument.Metadata`
   as `breadcrumb`/`heading`/`author`/`created_date`, alongside the existing
   `title`/`last_modified_date`. Stayed string-only on purpose — no
   typed-fields decision needed yet, that's item #3.
3. Reshape `ExtractionDocument`: keep `Metadata: Dictionary<string,string>`
   for plain scalars (title, author, breadcrumb/heading text), but add typed
   fields directly for structured/numeric data (`int TableCount`,
   `double? AverageWordConfidence`, `IReadOnlyList<string> FigureCaptions`)
   rather than round-tripping them through strings the way
   `last_modified_date` awkwardly does today.
   **Open decision**: confirm typed fields vs. keeping everything as strings
   in `Metadata` to minimize the diff, before implementing.
4. Rewrite `ChunkingService`:
   - Delete the six dead reads (`folder_path`, `quick_code`, `relative_path`,
     `check_date`, `version`, `summary`) — remove `Department`, `QuickCode`,
     `RelativePath`, `CheckDate`, `Version`, `Summary` from `ProtocolDocument`
     and stop setting them. Safe to drop from the C# model without touching
     the live index — Azure Search just leaves those fields unset for new
     docs; no migration needed to *remove* usage, only to *add* new fields.
   - Wire the real breadcrumb/heading into the currently-dead `Heading`
     field — reuses the existing (already indexed, already in semantic
     config) schema field for free. Fall back to `Structure.Headings` for
     docs with no bookmark outline.
   - `HeadingsDetected` in `ChunkingResults`/the run report becomes a real
     signal instead of a permanent zero, with no further change needed — it
     already reads `chunk.Heading != null`.
5. `EmbeddingText`/`Content`: fold the breadcrumb in the same way `Title` is
   already prepended, so it's live in retrieval immediately without waiting
   on a schema change.

## Tier 2 — needs the schema-update mechanism built first

Out of scope until that's unblocked:
- Dedicated `table_count`/`has_table`, `page_quality`, `figure_captions`
  fields for filtering/faceting, rather than folding them into text.

## Explicitly out of scope

- `Structure.Sections` (DI's real semantic chunk boundaries) — the eventual
  right answer for chunk *splitting*, not metadata. Belongs to the chunking
  strategy review, not this plan.
- `Structure.Boilerplate`-based header/footer stripping — a cleaning-stage
  concern (`PdfCleaner`), not chunking. `PdfCleaner`'s own comment already
  flags this as deferred pending real sample PDF confirmation, which we now
  have — worth a separate look, not bundled into this rewrite.

## Related docs

- `docs/chunking-pipeline.md` — the current "how it works" reference. It
  still describes the CSV-shaped metadata fields and the Zenya framing as if
  functioning; it will need a rewrite once this plan ships, not before.
- `docs/plan210726.md` — the four-stage indexing pipeline plan
  (extraction-skip, persistent archive, embedding dedup, rolling snapshot)
  this chunking rewrite sits alongside.
