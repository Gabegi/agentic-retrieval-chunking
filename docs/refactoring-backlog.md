# Refactoring backlog

Not scheduled into any stage yet — tracked here so they aren't lost.

## 1. Move PDF into its own class project

Split the PDF extraction/indexing code (currently
`src/agentic-rag-app/Services/Indexing/...`) out into a dedicated project,
mirroring how `CsvIndexing` already got moved out to its own standalone
project. Consistent with the no-generic, per-doc-type-split direction (see
memory: agentic-rag-app's pipeline is PDF-only, permanently — no swappable
sources).

## 2. Move all clients to a `Client` folder in `IndexingShared`

Consolidate the various Azure SDK client wiring — `SearchClient`/
`SearchIndexClient`, `DocumentIntelligenceClient`, `BlobServiceClient`/
`BlobContainerClient`, etc. — that's currently scattered across `program.cs`
and individual service constructors into one shared `Client` folder within
the `IndexingShared` project.
