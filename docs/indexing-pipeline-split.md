# Splitting the indexing pipeline per document type — proposed architecture

**Status: proposed, not yet implemented (as of 2026-07-17).** No `PdfIndexing`/
`CsvIndexing` projects exist yet. This doc records the plan so it can be
picked up without re-deriving it.

## Why

Today `src/agentic-rag-app` (`ProtocolsIndexer.csproj`) holds everything: the
Azure Functions/Durable Functions entrypoints *and* all the pipeline logic
(extraction, chunking, embedding, indexing, upload) for both CSV and PDF,
under one shared `IndexingFunction.cs` and one shared `IExtractionOrchestrator`
seam (see `docs/extraction-pipeline.md`).

PDF is being rebuilt standalone from CSV (see the "PDF extraction redesign"
work) because the two sources genuinely don't share shape — PDF's page
content and metadata come from one Document Intelligence call on one file,
while CSV joins two separate exports (`zenya_pages.csv` +
`zenya_index.csv`). Forcing both through one shared
`IExtractionOrchestrator`/chunking/embedding path means CSV-shaped
assumptions keep leaking into PDF work. The fix is to stop sharing the
pipeline at all, not just the extraction step.

## Target shape

```
src/
  agentic-rag-app/                         (ProtocolsIndexer.csproj — Functions host, thin)
    Functions/
      PdfIndexingFunction.cs               ← StartIndexing HTTP trigger, PdfIndexingOrchestrator,
                                              ExtractActivity/ChunkActivity/EmbedAndUploadActivity/
                                              SaveIndexReportActivity — delegating into PdfIndexing.*
      CsvIndexingFunction.cs               ← same shape, delegating into CsvIndexing.*
      QueryingFunction.cs                  ← unchanged, doc-type agnostic (reads the merged index)
    Configuration/                         ← DI wiring: AddPdfIndexing() + AddCsvIndexing()
    Observability/                         ← stays here (cross-cutting: OTel, IRunReportWriter)
    program.cs / host.json / appsettings.*

  PdfIndexing/                             (new class library, PdfIndexing.csproj)
    Extraction/  Chunking/  Embedding/  Indexing/  Upload/  Models/
    ServiceCollectionExtensions.cs         ← AddPdfIndexing(this IServiceCollection)

  CsvIndexing/                             (new class library, CsvIndexing.csproj)
    Extraction/  Chunking/  Embedding/  Indexing/  Upload/  Models/
    ServiceCollectionExtensions.cs         ← AddCsvIndexing(this IServiceCollection)
```

`agentic-rag-app` becomes just the Functions/Durable Function host — one
orchestration flow per document type. All actual logic moves into a
class library per document type: extraction, chunking, embedding, indexing,
and upload all become pipeline-specific, not just extraction. This maps the
current `Services/Indexing/Extraction/{PDFExtraction,CSVExtraction}` split
out one level further, pulling `ChunkingService`/`EmbeddingService`/
`IndexService`/`UploadService` (currently shared in `Services/Indexing/`)
apart into per-pipeline copies inside each new library.

No shared "common" indexing library is planned. Duplication of
chunking/embedding/index/upload logic across `PdfIndexing` and `CsvIndexing`
is an accepted, deliberate tradeoff — not an oversight — in exchange for each
pipeline being free to evolve its own shape without touching the other.

**Models are the exception**: some `Models/` types will be shared, some
won't. Which ones is still an open decision, to be made when the split
actually happens — don't assume all-shared or all-forked.

## Open questions (unresolved, resolve before implementing)

1. **Naming collision.** `PdfExtractionOrchestrator` is currently a plain C#
   class (extraction pipeline steps) under
   `Services/Indexing/Extraction/PDFExtraction/`. Once split, "Orchestrator"
   in the Functions project means a Durable Function orchestrator
   (`PdfIndexingOrchestrator`). Consider renaming the class-library one (e.g.
   `PdfExtractionPipeline`) so the two "orchestrator" concepts don't read as
   the same thing.
2. **`IndexRunReport`.** Today one shared record aggregates
   `ExtractionResults`/`ChunkingResults`/`EmbedUploadingResults` from both
   sources. Undecided whether this forks into `PdfIndexRunReport`/
   `CsvIndexRunReport` living in each library, or stays a shared reporting
   shape (arguably an output/observability concern, not pipeline logic) —
   ties into the "some models shared, some not" question above.

## How to apply

Before implementing: re-confirm which specific models are meant to stay
shared (ask, don't assume) and resolve the two open questions above. Related
context on the PDF-specific rebuild (independent of this split) lives in
`docs/extraction-pipeline.md` and `docs/chunking-pipeline.md`.
