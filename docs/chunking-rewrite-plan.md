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
3. ✅ `ExtractionDocument` fully reshaped — **no `Metadata` dictionary at
   all**, eliminated entirely (not kept alongside typed fields). Every piece
   of extraction data worth carrying is a named, typed field: file-level
   (`Title`, `Author`, `CreatedAt`, `PageCount`, `LastModifiedDate`,
   `Bookmarks`, `Sections` — duplicated identically across every page of one
   file, same as `Title` always was) and page-level, filtered to that page's
   `PageNumber` (`Breadcrumb`, `Headings`, `Boilerplate`, `Tables`,
   `Dimensions`, `SelectionMarks`, `Figures`, `Lines`,
   `AverageWordConfidence`). Deliberately **not** carried:
   `FileSizeBytes`/`PdfSpecVersion`/`EstimatedCostUsd` (already in the run
   report) and `PageErrors`/`Warnings` (already fully surfaced through
   `PdfPipelineValidator` → `ExtractionOutput.Issues` → the run report) —
   duplicating either would repackage identical data, not add anything new.
   Rationale for going all-typed rather than typed-fields-plus-dictionary:
   a string-keyed dictionary is exactly what let six CSV-era fields sit
   unused for so long without anyone noticing — nothing forced a reader to
   account for every key. A fully typed record does.
4. ✅ `ProtocolDocument` renamed to **`DocumentChunk`** (`ProtocolDocument`
   was Zenya/CSV-era naming — "care protocols" specifically — for a project
   that's PDF-only now) and restructured to match. Search-indexed fields
   (`Id`, `DocumentId`, `Title`, `LastModifiedDate`, `Content`, `Heading`,
   `PageNumber`, `ChunkIndex`, `ContentVector`) stay `[JsonPropertyName]`
   mapped; `Department`/`QuickCode`/`RelativePath`/`CheckDate`/`Version`/
   `Summary` removed entirely (dead for PDF, safe to drop without touching
   the live index — Azure Search just leaves unset fields null for new
   docs). Every other new field from `ExtractionDocument` (`Author`,
   `CreatedAt`, `PageCount`, `Bookmarks`, `Sections`, `Breadcrumb`,
   `Headings`, `Boilerplate`, `Tables`, `Dimensions`, `SelectionMarks`,
   `Figures`, `Lines`, `AverageWordConfidence`) is carried through too,
   `[JsonIgnore]`d (no matching Search schema field yet — Tier 2) but
   available in the Stage 2 archive today.
   `IndexService`'s schema and semantic config updated to match (dead
   fields removed, including `summary` from `ContentFields`).
5. ✅ `ChunkingService` rewritten: `Heading` = `Breadcrumb ?? Headings.FirstOrDefault()?.Content`,
   prepended into `Content` the same way `Title` already was — real content
   in the previously-always-null `Heading` field, and `HeadingsDetected` in
   `ChunkingResults`/the run report becomes a real signal instead of a
   permanent zero, no further change needed since it already reads
   `chunk.Heading != null`. `EmbeddingText` simplified to `=> Content`
   directly (no more `Summary` fold-in — that field is gone).

## Tier 2 — dedicated fields for filtering/faceting ✅ done

Correction to the original framing above: adding a *new* field to an Azure
AI Search index doesn't need a migration mechanism — `CreateOrUpdateIndex`
supports pure additions natively, existing documents just get `null` until
reprocessed. The real blocker was `IndexService.EnsureIndexAsync` itself,
which deliberately skips updating an index that already exists (to avoid a
code push silently clobbering portal-side customisations nobody told it
about) — and since no index exists yet, that guard never triggered here.

- `IndexService`: added `table_count` (Int32), `has_table` (Boolean,
  filterable+facetable), `page_quality` (Double, filterable+sortable),
  `figure_captions` (searchable string collection, `nl.microsoft` analyzer).
- `DocumentChunk`: four new `[JsonPropertyName]`-mapped computed properties
  (`TableCount`, `HasTable`, `PageQuality`, `FigureCaptions`) derived from
  the raw `Tables`/`Figures`/`AverageWordConfidence` fields already carried
  through since Tier 1 — same pattern as `TokenEstimate`/`IsEmpty` already
  being computed from `Content`.
- Same operational caveat as the Stage 4 snapshot: these only populate for
  documents that actually get reprocessed. A `force=true` reindex is what
  backfills them for the whole corpus, not just new/updated docs going
  forward.
- Deliberately not built: an "update an existing index" path. Not needed
  yet (no index exists), and building it speculatively risks exactly the
  portal-customisation-clobbering problem `EnsureIndexAsync`'s current skip
  guards against. Worth adding once there's a real index and a real next
  field to add.

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
