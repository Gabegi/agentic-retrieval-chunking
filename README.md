# agentic-retrieval-chunking

Agentic Retrieval with Chunking strategies in .NET and Terraform using Azure AI Foundry and Azure AI Search.

## What this is

A RAG pipeline for Cordaan's internal Dutch-language document corpus (protocols, policies, work instructions), built on Azure AI Search's **agentic retrieval** (knowledge base) feature — the knowledge base plans its own search queries and synthesizes the final answer, so there's no separate hand-rolled retrieval + chat step in production.

## Projects

- **`src/agentic-rag-app`** (`ProtocolsIndexer`) — Azure Functions app with two responsibilities:
  - **Indexing pipeline**: extracts documents (CSV-based extraction), chunks them (`ChunkingStrategy1` — sentence-aware sliding window), embeds and writes them to Azure AI Search, and provisions the knowledge source/knowledge base (`KnowledgeService`).
  - **Querying** (`QueryingFunction`, `POST /api/query`): answers questions via `AgenticRagQueryService`, which orchestrates a knowledge base retrieval call and delegates the rest to focused collaborators:
    - `KnowledgeBaseReferenceMapper` — parses knowledge base references into `RetrievedChunk`s.
    - `ChunkNeighborExpander` — fetches neighboring pages via a raw Search side-channel when an answer likely continues onto an adjacent page (page-boundary continuation fix).
    - `KnowledgeBaseActivitySummary` — sums token usage from the knowledge base's per-step activity records.
  - `Services/Querying/Classic/RagQueryService.cs` is the older one-shot hybrid-search-plus-chat path. It's parked (not wired into DI) in favor of agentic retrieval, kept around in case it's revisited.

- **`src/RagApp.Evaluation.Tests`** — MSTest eval harness. `EvaluateGoldenQuery` runs every scenario in `testdata/golden-questions.json` through `AgenticRagQueryService` and scores it with Groundedness/Relevance/Coherence/Equivalence/Retrieval/F1 evaluators (`Microsoft.Extensions.AI.Evaluation`). Only Groundedness gates the build; the rest are tracked as trends. F1 is skipped (scored `-1`) for scenarios flagged `AnswerableFromCorpus: false` — known corpus gaps where the expected behavior is abstention, not a lexical match.

- **`src/scraper`** — scrapes source protocol documents ahead of indexing.

- **`infra/`** — Terraform for the full stack: resource group, Azure AI Search, Azure OpenAI, Document Intelligence, Storage, the indexer Function App, Log Analytics/monitoring.

## Running the eval

```
dotnet test src/RagApp.Evaluation.Tests/RagApp.Evaluation.Tests.csproj -c Release --filter "TestCategory=golden"
```

Requires `SEARCH_ENDPOINT`, `OPENAI_ENDPOINT`, `OPENAI_GPT_DEPLOYMENT`, etc. as environment variables (see `RagEvaluationTests.Env`/`Defaults` for the full list and dev fallbacks) and an Azure identity with Search/OpenAI access.

## CI

- `1-deploy-infrastructure.yml` — `terraform apply`.
- `2-scrape-protocols.yml` — runs the scraper.
- `3-deploy-application.yml` — deploys the Function App.
- `4-evaluate-rag.yml` — runs the golden eval suite against live infra.

## Current status / known issues

- **Quota**: the subscription used for `infra/` (`Visual Studio Enterprise Subscription – MPN`) currently has 0 TPM quota for `gpt-4.1` and `gpt-4o`, and 0 quota for the `P1v3` App Service Plan, in East US. As a workaround, the Azure OpenAI account (`azurerm_cognitive_account.openai` in `openai.tf`) is pinned to **West Europe** independently of the resource group's `eastus` location — Search/Storage/Function App stay in East US, only OpenAI moved. The App Service Plan quota issue is unresolved and separate (unrelated to region — it's the Function App's compute plan, still in East US).
- **`golden-questions.json`** doesn't yet have `AnswerableFromCorpus` set on its known corpus-gap scenarios (e.g. "verschilt per locatie" answers) — pending review.
