# Vertical-slice restructure of cap.lz.app

Status: proposed, not yet implemented. No code changes have been made — this
document is the plan only.

## Context

The codebase (`src/`) has grown organically and the structure no longer reflects
how the two indexing flows (PDF, CSV) actually behave. PDF was already rebuilt as a
fully standalone pipeline (see `docs/plan210726.md`'s "no generic" note), and CSV was
already split into its own `CsvIndexing` class library — but the split is
half-finished: CSV has no host wiring at all (dormant, unit-tested only), and PDF
quietly stopped depending on the one "shared" project (`IndexingShared`) that was
supposed to unify them, instead growing its own duplicate local copies of
`Configuration`/`Observability`/`Models`.

This plan was requested as a 5-point vertical-slicing instruction from the user:
1. Rename `agentic-rag-app` → `AgenticRagApp`.
2. Create `Infrastructure` project, move all Azure SDK clients into it under a
   `Clients` folder.
3. Give each indexing flow its own project (`Indexing.Csv`, `Indexing.Pdf`).
4. Give every project its own `.Tests` project.
5. Move Observability/metrics/reports into their own project.

Investigating the actual repo state (not just the instruction text) turned up prior
planning docs already recording related intent — `docs/indexing-pipeline-split.md`,
`docs/refactoring-backlog.md`, `docs/plan210726.md` — plus real bugs and divergence
this plan needs to account for.

### Input from `docs/refactoring-backlog.md`

That doc records two unscheduled backlog items, both folded into this plan:

1. *"Move PDF into its own class project ... mirroring how `CsvIndexing` already got
   moved out to its own standalone project. Consistent with the no-generic,
   per-doc-type-split direction."* → this is Phase 5 below.
2. *"Move all clients to a `Client` folder in `IndexingShared`"* → the backlog doc
   proposed putting this inside the existing `IndexingShared` project. This plan
   instead retires `IndexingShared` entirely and puts the client folder in a new
   `AgenticRagApp.Infrastructure` project (see "Resolved decisions" #4) — a
   deliberate deviation from the backlog note, confirmed with the user, not an
   oversight.

### Other repo findings that shape this plan

- **Real duplication, not just naming**: `agentic-rag-app/Configuration/IndexerConfig.cs`
  vs `IndexingShared/Configuration/IndexerConfig.cs` are byte-identical except
  namespace. Same for `IRunReportWriter`. `agentic-rag-app` (`AgenticRag.csproj`) has
  **no project reference to `IndexingShared` at all** — it duplicated everything
  locally instead of depending on the shared project.
- **Real divergence**: `agentic-rag-app`'s `ExtractionDocument`/`IndexRunReport`/
  `ChunkStats` have been rewritten with PDF-specific typed fields and no longer match
  `IndexingShared`'s versions (which CSV still uses, generic
  `Dictionary<string,string>` metadata shape). These are not safe to silently merge
  back into one shared type.
- **A real bug**: `SearchIndexClient` is constructed with `new SearchIndexClient(...)`
  independently **5 separate times** (PDF `IndexService.cs`, PDF
  `IndexDocumentService.cs`, CSV `IndexService.cs`, CSV `IndexDocumentService.cs`,
  `KnowledgeService.cs`) from the same config+credential, instead of once via DI.
  `SearchClient` has the same problem in both `IndexDocumentService.cs` files
  (constructed locally, bypassing the one DI-registered instance in `Program.cs`).
- **Dead code**: `Services/Querying/Classic/RagQueryService.cs` is not registered in
  DI (`AgenticRagQueryService` is what's actually wired) but still has a test file
  (`RagQueryServiceTests.cs`).
- **Dormant flow**: `CsvIndexing`'s own `ServiceCollectionExtensions.cs` comment says
  it directly: *"Not called from Program.cs yet: CSV has no active Durable flow
  today."* Infra (`infra/function_app.tf`) defines exactly one
  `azurerm_windows_function_app` — one deployable, not one per doc type.
- **Hardcoded paths** that must be updated on rename: `.pipelines/4-deploy-application.yml`
  (`dotnet publish src/agentic-rag-app/AgenticRag.csproj`), `.pipelines/3-build-test.yml`
  (`dotnet test src/RagApp.UnitTests/RagApp.UnitTests.csproj`).

## Resolved decisions (confirmed with user)

1. **Naming convention**: `AgenticRagApp.*` for every new project (not
   `Agentic.RagApp.*`, which appeared inconsistently in the original instruction).
2. **"Each indexing flow is an App"** means a Clean-Architecture application-layer
   class library composed into the **one existing** Azure Function host via
   `AddPdfIndexing()`/`AddCsvIndexing()` extension methods (mirrors the pattern
   `CsvIndexing` already uses) — **not** separate deployable Function Apps. No
   Terraform or pipeline-stage changes needed for this.
3. **Cleanup is in scope**: fold in the duplication/dead-code fixes found above, not
   just relocate them as-is.
4. **`IndexingShared`'s fate**: dissolved. Its contents redistribute into three new
   projects (Domain / Infrastructure / Observability) per the target shape below —
   no project named `IndexingShared` survives, and the backlog doc's "Client folder
   inside IndexingShared" suggestion is superseded by putting it in the new
   `Infrastructure` project instead.
5. **New `AgenticRagApp.Domain` project** (user's addition, not in the original
   5-point list): holds genuinely shared model types, including abstract/extensible
   base types other flows derive from. This needs real design work, not just a file
   move — see "Domain project scope" below.
6. **Orchestrator naming collision**: rename the class-library pipeline class
   `PdfExtractionOrchestrator` → `PdfExtractionPipeline`, so "Orchestrator" is
   reserved for the Durable Function orchestrator concept in the host project once
   PDF gets its own project.
7. **Reporting**: "We should never mix different document types in reporting" — fork
   `IndexRunReport` into **`PdfIndexRunReport`** and **`CsvIndexRunReport`**, sharing
   only a common envelope (see Domain scope below), never one combined shape.

## Target project structure

```
src/
  AgenticRagApp/                    (host — thin: Functions, Querying, composition root)
    Functions/
      PdfIndexingFunction.cs        ← StartIndexing HTTP trigger + Durable orchestrator + activities, delegates into Indexing.Pdf
      CsvIndexingFunction.cs        ← same shape, delegates into Indexing.Csv (see "CSV wiring" open item)
      QueryingFunction.cs           ← unchanged, doc-type agnostic (reads the merged index)
    Services/Querying/              ← AgenticRagQueryService, KnowledgeService, ChunkNeighborExpander (stays — cross-cutting, reads one shared Search index)
    Program.cs                     ← wires Infrastructure + Observability + AddPdfIndexing() + AddCsvIndexing()
    (dead Services/Querying/Classic/RagQueryService.cs deleted, not moved)

  AgenticRagApp.Domain/              (new — shared/abstract model types + cross-cutting ports)
    Reports/                        ← shared report envelope, IRunReportWriter
    Storage/                        ← IArtifactStore (port: WriteJsonAsync/ReadJsonAsync/DeleteAsync/ListAsync) — the abstraction Observability codes against instead of BlobContainerClient directly

  AgenticRagApp.Infrastructure/      (new — all Azure SDK client wiring)
    Clients/                        ← one AddAgenticRagAppInfrastructure() DI extension: BlobServiceClient, keyed pipeline-temp BlobContainerClient, SearchClient, SearchIndexClient (newly DI-registered — fixes the 5x duplication bug), DocumentIntelligenceClient, AzureOpenAIClient (+ IEmbeddingGenerator/IChatClient wrapping)
    Storage/                        ← BlobArtifactStore : IArtifactStore (Domain's port), the one place BlobContainerClient touches Observability's concern
    Configuration/                  ← IndexerConfig (deduped, one copy)
    Models/                         ← infra-facing DTOs only if any emerge (e.g. Search index field schema), not domain models

  AgenticRagApp.Observability/       (new — depends on Domain only, no Azure SDK reference)
    Reports/                        ← RunReportWriter (implements Domain's IRunReportWriter against IArtifactStore, not BlobContainerClient), PdfIndexRunReport, CsvIndexRunReport (forked per decision #7)
    Interfaces/ Models/             ← IPipelineArtifactWriter/PipelineArtifactWriter, ISnapshotService/SnapshotService (both coded against IArtifactStore), Instrumentation, ChunkStats/ExtractionStats/EmbedUploadStats/SnapshotChunk

  AgenticRagApp.Indexing.Csv/         (renamed from CsvIndexing)
    Extraction/ Chunking/ Embedding/ Indexing/ Upload/ Models/
    ServiceCollectionExtensions.cs   ← AddCsvIndexing() — updated to pull clients from Infrastructure instead of expecting the host to pre-register them ad hoc

  AgenticRagApp.Indexing.Pdf/         (new — pulled out of AgenticRagApp)
    Extraction/  (PdfExtractionPipeline, DocumentIntelligenceExtractor, PDFDocumentAnalyzer, PdfCleaner, PdfDocumentValidator, PdfNativeMetadataExtractor, PDFSectionBreadCrumbBuilder, PdfPipelineValidator)
    Chunking/ (ChunkingService, ChunkingStrategy1/2)  Embedding/ (EmbeddingService, VectorCache)  Indexing/ (IndexService, IndexDocumentService, UploadService)  Models/
    ServiceCollectionExtensions.cs   ← AddPdfIndexing(), same shape as CSV's

  AgenticRagApp.Tests/                (was RagApp.UnitTests, split per project)
  AgenticRagApp.Domain.Tests/
  AgenticRagApp.Infrastructure.Tests/
  AgenticRagApp.Observability.Tests/
  AgenticRagApp.Indexing.Csv.Tests/
  AgenticRagApp.Indexing.Pdf.Tests/
  RagApp.Evaluation.Tests/            (unchanged in shape — golden-question end-to-end eval, not per-slice; just repoint its ProjectReference)
```

Dependency direction: `Domain` has no project references — it owns the shared model
types *and* the cross-cutting ports (`IArtifactStore`, `IRunReportWriter`) that other
projects code against. `Infrastructure` references `Domain` and implements those
ports against real Azure SDK clients. `Observability` also references only `Domain`
— it depends on `IArtifactStore`, never on `BlobContainerClient` or `Infrastructure`
directly, so its report/stat logic is unit-testable with a fake store and has zero
Azure SDK dependency. This was originally drafted as `Observability → Infrastructure`
(to get a blob client) but that inverts the usual layering and was flagged as the one
arrow likely to age badly — fixed by moving the port to `Domain` instead.
`Indexing.Csv`/`Indexing.Pdf` reference `Domain`, `Infrastructure`, `Observability`.
`AgenticRagApp` (host) references all five, is the only place that wires
`Infrastructure`'s `BlobArtifactStore` against `Observability`'s consumers, and is
the only composition root.

### Domain project scope (needs care, not a mechanical move)

`ExtractionDocument`/`ProtocolDocument` have already diverged in shape and purpose
between PDF (rich typed fields) and CSV (generic `Dictionary<string,string>`
metadata) — **do not** force these into one shared/abstract type; each stays local to
its own `Indexing.*` project. The concrete, safe starting scope for `Domain` is a
**shared report envelope** (`InstanceId`, `StartedAt`, `FinishedAt`, `Success`,
`ErrorMessage` — the fields every run report needs regardless of doc type) that
`PdfIndexRunReport` and `CsvIndexRunReport` both derive from/implement, satisfying
both decision #5 (abstract, extensible) and #7 (never mixed) at once. Any other
candidate for `Domain` gets decided file-by-file during Phase 1, not assumed up
front.

`Domain` also owns the `IArtifactStore` port (see dependency-direction note above) —
this is a deliberate, narrow addition to Domain's scope to keep `Observability`
Azure-SDK-free, not a general invitation to put infrastructure abstractions there.

### Open item to confirm before Phase 5 (CSV wiring)

The target shape above includes `CsvIndexingFunction.cs` — actually wiring CSV into a
live Durable Function for the first time. That's a **behavior** change (CSV starts
running in production), not just a restructuring one. Recommend scaffolding
`CsvIndexingFunction.cs` following `PdfIndexingFunction.cs`'s shape, but leaving it
registered/deployed-or-not as a separate explicit decision at that point — flag for
confirmation when Phase 5 starts, don't silently activate it.

## Execution approach

This is a large, multi-day structural change touching every project in the solution.
Execute it as the phases below, **one phase per commit/checkpoint**, and stop for
confirmation after each phase before starting the next (confirm scope on big
refactors, execute literally, verify with `dotnet build`/`dotnet test`, not
guesswork). Nothing in this plan is implemented yet — implementation starts only when
explicitly requested.

**Phase 0 — Rename host, delete dead code, introduce shared build props.**
`agentic-rag-app/` → `AgenticRagApp/`, `AgenticRag.csproj` → `AgenticRagApp.csproj`,
namespace `AgenticRag` → `AgenticRagApp` throughout, update `ragapplication.sln`
project entries and paths, update `.pipelines/4-deploy-application.yml`'s publish
path. Delete `Services/Querying/Classic/RagQueryService.cs` and its test
(`RagQueryServiceTests.cs`) — confirmed dead, not wired into DI.

Rename blast-radius checklist (grep the whole repo, not just `*.cs`): `host.json`
(Durable Task Hub name may be derived from/reference the old app name),
`local.settings.json` / `appsettings*.json`, `Properties/launchSettings.json`
(profile name), any App Insights cloud-role-name or dashboard/query string literal
keyed on `AgenticRag`/`agentic-rag-app`/`protocols-indexer`, and `infra/*.tf` /
`.pipelines/*.yml` for any other path or name reference beyond the two already
identified.

Also add `src/Directory.Build.props` (common `TargetFramework`/`Nullable`/
`ImplicitUsings`) and `src/Directory.Packages.props` (central package version
management) now, before the six new csproj files get created in later phases, so
they inherit consistent settings instead of copy-pasting boilerplate and drifting on
package versions.

Verify: `dotnet build src/ragapplication.sln`, `dotnet test src/RagApp.UnitTests/...`.

**Phase 1 — `AgenticRagApp.Domain`.**
New class library, no project references. Add the shared report envelope described
above. Move `IndexingShared`'s `Models/` types here only if still needed after Phase
3's report fork (re-check `ExtractionOutput`/`ProtocolDocument` usage at that point —
they may turn out to be CSV-only and belong in `Indexing.Csv` instead).
Verify: builds standalone with zero dependencies beyond BCL.

**Phase 2 — `AgenticRagApp.Infrastructure`.**
New class library. One `AddAgenticRagAppInfrastructure()` DI extension registering
every Azure SDK client (see Clients table above), including a proper
`SearchIndexClient` registration (new — didn't exist before). Move `IndexerConfig`
here (one copy). Update the 5 call sites that currently
`new SearchIndexClient(...)`/`new SearchClient(...)` locally to accept an injected
client instead. Delete `IndexingShared/Configuration`.

Client lifetimes, stated explicitly so the isolated-worker host doesn't get this
wrong silently: all Azure SDK clients (`BlobServiceClient`, `SearchClient`,
`SearchIndexClient`, `DocumentIntelligenceClient`, `AzureOpenAIClient`,
`KnowledgeBaseRetrievalClient`) are thread-safe by design and registered as
**singletons**. The keyed `"pipeline-temp"` `BlobContainerClient` stays a singleton
per key, same as today.

This phase edits live PDF-pipeline code (the 5 call sites), not just adds new files
— it's a behavioral change to the active flow, even though unit tests mock these
clients directly. Don't wait until Phase 5 to catch a DI-lifetime bug here.
Verify: `dotnet build`, run existing `IndexService`/`IndexDocumentService`/
`KnowledgeService` unit tests, **and** run a local `StartIndexing` trigger
(Azure Functions Core Tools against dev settings) end-to-end once — Moq-based unit
tests won't surface a singleton-vs-per-invocation client lifetime mistake.

**Phase 3 — `AgenticRagApp.Observability`.**
New class library, depends on `Domain` **only** (see dependency-direction note — no
reference to `Infrastructure` or any Azure SDK package). Move `IRunReportWriter`
(interface, to `Domain`) / `RunReportWriter` (implementation, to `Observability`,
rewritten against `Domain`'s `IArtifactStore` instead of `BlobContainerClient` —
dedupe the `AgenticRag`-local and `IndexingShared` copies into this one),
`IPipelineArtifactWriter`/`PipelineArtifactWriter`, `ISnapshotService`/
`SnapshotService` (both likewise rewritten against `IArtifactStore`),
`Instrumentation`, stat models. Fork `IndexRunReport` into
`PdfIndexRunReport`/`CsvIndexRunReport` per decision #7, both deriving from Phase 1's
shared envelope. Update `IndexingFunction.cs`'s `BuildReport` and CSV's
report-writing call sites accordingly. Delete `IndexingShared/Observability`. In the
host, wire `Infrastructure`'s `BlobArtifactStore` against `IArtifactStore` in DI.
Verify: `dotnet build`, `RunReportWriterTests`/`SnapshotService` tests pass against
the consolidated types using a fake `IArtifactStore` (no Azure SDK needed to test
this project at all — that's the point of the port).

**Phase 4 — Rename `CsvIndexing` → `AgenticRagApp.Indexing.Csv`.**
Folder/csproj/namespace/sln rename. Repoint its project references at `Domain`,
`Infrastructure`, `Observability` instead of the old `IndexingShared`. Update
`ServiceCollectionExtensions.AddCsvIndexing()` accordingly. Delete `IndexingShared`
project and its sln entry (now empty).
Verify: `dotnet build`, existing `CsvExtraction`/`Indexing` tests pass unchanged.

**Phase 5 — Extract `AgenticRagApp.Indexing.Pdf`.**
Confirm the CSV-wiring question above first. Move all PDF-specific pipeline code out
of the host: `Services/Indexing/Extraction/*` (renaming `PdfExtractionOrchestrator` →
`PdfExtractionPipeline` per decision #6), `ChunkingService`/`ChunkingStrategy*`,
`EmbeddingService`, `IndexService`, `UploadService`, `IndexDocumentService`,
`Embedding/VectorCache`, their `Models`/`Interfaces`. Add
`ServiceCollectionExtensions.AddPdfIndexing()` mirroring CSV's. Host `Program.cs`
shrinks to composition only. Split `IndexingFunction.cs` into
`Functions/PdfIndexingFunction.cs` (+ scaffold `CsvIndexingFunction.cs` per the open
item) and keep `QueryingFunction.cs` at the host level.
Verify: full solution build, full existing PDF-path tests pass, manually trigger
`StartIndexing` locally against dev settings to confirm the Durable flow still runs
end-to-end (this phase has the highest regression risk — it moves the active
production pipeline).

**Phase 6 — Split tests per project.**
`RagApp.UnitTests` is already internally organized by slice (`Indexing/`, `Querying/`,
`Observability/`, `CsvExtraction/`, `PdfExtraction/`, `Functions/`) — map each folder
to its new `AgenticRagApp.<X>.Tests` project. Before writing test files: `dotnet
test` has no clean "exclude this project" flag, so a solution-wide test run that
tries to skip `RagApp.Evaluation.Tests` is fragile. Decided approach: replace the
single `dotnet test` line in `.pipelines/3-build-test.yml` with **one explicit
`dotnet test` call per new test project**, all writing into the same
`--results-directory` (coverlet already namespaces each run's `coverage.cobertura.xml`
under a per-run GUID subfolder, so the existing double-`**` glob in
`PublishCodeCoverageResults@2` keeps working unmodified). Repoint
`RagApp.Evaluation.Tests`'s `ProjectReference` at the renamed
`AgenticRagApp.csproj`. Delete `RagApp.UnitTests` once emptied.
Verify: `dotnet test --list-tests` on `RagApp.UnitTests` before the move, and on the
full set of new test projects after — diff the two lists rather than just comparing
counts, so a test that moved but silently lost discovery (bad trait/category, wrong
namespace) doesn't slip through. Then confirm all green and the coverage publish
step in the pipeline still resolves its glob.

**Phase 7 — Update stale docs, record the "why" as an ADR.**
`docs/indexing-pipeline-split.md`, `docs/refactoring-backlog.md`,
`docs/plan210726.md` (and any other doc referencing `agentic-rag-app`/`CsvIndexing`/
`IndexingShared` by old name) get marked done/superseded or path-corrected, so a
future session doesn't re-derive stale intent from them the way this session had to
reconcile it. Also write a short ADR (e.g. `docs/adr/0001-no-shared-extraction-document.md`)
capturing why `ExtractionDocument`/`ProtocolDocument` were deliberately **not**
unified into one shared `Domain` type despite the vertical-slice restructure — the
divergence reasoning in "Domain project scope" above — so this doesn't get
re-litigated by a future session that only sees two similarly-named types in two
different projects and assumes it's an oversight.

## Verification (end to end, after all phases)

- `dotnet restore` + `dotnet build` on the renamed solution, zero errors/new
  warnings.
- `dotnet test` across every new `.Tests` project, all green, same test count as
  before the split (no test silently dropped in the move).
- Diff `git status` / `git diff --stat` phase by phase to confirm each phase touches
  only its intended files.
- Manually run the PDF indexing Durable flow locally (Azure Functions Core Tools) at
  least once after Phase 5 to confirm no behavioral regression — this is the one
  phase that moves live, running code rather than dormant/duplicate code.
