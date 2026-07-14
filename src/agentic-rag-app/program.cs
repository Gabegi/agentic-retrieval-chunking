using Azure.AI.DocumentIntelligence;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Search.Documents.KnowledgeBases;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Configuration;
using ProtocolsIndexer.Observability;
using ProtocolsIndexer.Observability.Reports;
using ProtocolsIndexer.Services;

// Dev-only entry point: compares PdfPig vs Document Intelligence extraction over a
// sample blob container, using the same production PdfJoiner/PdfCleaner/
// PdfPipelineValidator the real pipeline will use — see docs/pdf-extraction-pipeline.md.
// Runs a lightweight, standalone host instead of the Functions worker host, so it can
// be invoked locally (`dotnet run -- --compare-pdf-backends <containerName>`) without
// deploying. Left in permanently as a re-validation tool, not removed once a backend
// is chosen.
if (args.Contains("--compare-pdf-backends"))
{
    await RunPdfBackendComparisonAsync(args);
    return;
}

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((ctx, services) =>
    {
        // Fail fast with a named list of missing settings, rather than letting a missing
        // value surface later as an obscure NullReferenceException or UriFormatException.
        var requiredKeys = new[]
        {
            "SEARCH_ENDPOINT",
            "OPENAI_ENDPOINT",
            "OPENAI_EMBEDDING_DEPLOYMENT",
            "OPENAI_GPT_DEPLOYMENT",
            "OPENAI_GPT_MODEL_NAME",
            "STORAGE_ACCOUNT_URL",
            "SEARCH_INDEX_NAME",
            "KNOWLEDGE_SOURCE_NAME",
            "KNOWLEDGE_BASE_NAME",
            "APPLICATIONINSIGHTS_CONNECTION_STRING",
            "AzureWebJobsStorage:accountName",
        };
        var missingKeys = requiredKeys.Where(k => string.IsNullOrWhiteSpace(ctx.Configuration[k])).ToList();
        if (missingKeys.Count > 0)
            throw new InvalidOperationException(
                $"Missing required app setting(s): {string.Join(", ", missingKeys)}. " +
                "Set these in local.settings.json (local) or the Function App configuration (deployed).");

        var config = new IndexerConfig
        {
            SearchEndpoint               = ctx.Configuration["SEARCH_ENDPOINT"]!,
            OpenAiEndpoint               = ctx.Configuration["OPENAI_ENDPOINT"]!,
            OpenAiEmbeddingDeployment    = ctx.Configuration["OPENAI_EMBEDDING_DEPLOYMENT"]!,
            OpenAiGptDeployment          = ctx.Configuration["OPENAI_GPT_DEPLOYMENT"]!,
            OpenAiGptModelName           = ctx.Configuration["OPENAI_GPT_MODEL_NAME"]!,
            OpenAiExtractionDeployment   = ctx.Configuration["OPENAI_EXTRACTION_DEPLOYMENT"] ?? "gpt-41-extraction",
            DocumentIntelligenceEndpoint = ctx.Configuration["DOCUMENT_INTELLIGENCE_ENDPOINT"] ?? "",
            StorageAccountUrl            = ctx.Configuration["STORAGE_ACCOUNT_URL"]!,
            StorageContainer             = ctx.Configuration["STORAGE_CONTAINER"] ?? "protocols",
            SearchIndexName              = ctx.Configuration["SEARCH_INDEX_NAME"]!,
            KnowledgeSourceName          = ctx.Configuration["KNOWLEDGE_SOURCE_NAME"]!,
            KnowledgeBaseName            = ctx.Configuration["KNOWLEDGE_BASE_NAME"]!,
            OpenAiEmbeddingModelName     = ctx.Configuration["OPENAI_EMBEDDING_MODEL_NAME"] ?? "text-embedding-3-large",
            OpenAiEmbeddingDimensions    = int.TryParse(ctx.Configuration["OPENAI_EMBEDDING_DIMENSIONS"], out var dims) ? dims : 3072,
        };

        TokenCredential credential = new DefaultAzureCredential();

        services.AddSingleton(config);
        services.AddSingleton(credential);

        services.AddSingleton(_ =>
            new BlobServiceClient(new Uri(config.StorageAccountUrl), credential));

        // Pipeline temp storage — passes large payloads between Durable activities via blob
        // rather than through Durable Table Storage (64KB row-size limit).
        services.AddKeyedSingleton<BlobContainerClient>("pipeline-temp", (_, _) =>
        {
            var accountName = ctx.Configuration["AzureWebJobsStorage:accountName"]!;
            return new BlobServiceClient(
                new Uri($"https://{accountName}.blob.core.windows.net"),
                credential)
                .GetBlobContainerClient("indexing-pipeline");
        });

        services.AddSingleton(_ =>
            new AzureOpenAIClient(new Uri(config.OpenAiEndpoint), credential));

        services.AddEmbeddingGenerator(sp =>
            sp.GetRequiredService<AzureOpenAIClient>()
              .GetEmbeddingClient(config.OpenAiEmbeddingDeployment)
              .AsIEmbeddingGenerator())
            .UseOpenTelemetry(sourceName: "Microsoft.Extensions.AI", configure: c => c.EnableSensitiveData = true);

        services.AddChatClient(sp =>
            sp.GetRequiredService<AzureOpenAIClient>()
              .GetChatClient(config.OpenAiGptDeployment)
              .AsIChatClient())
            .UseOpenTelemetry(sourceName: "Microsoft.Extensions.AI", configure: c => c.EnableSensitiveData = true);

        var appInsightsConnectionString = ctx.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]!;

        services.AddOpenTelemetry()
            .UseFunctionsWorkerDefaults()
            .ConfigureResource(r => r.AddService(
                serviceName:    "protocols-indexer",
                serviceVersion: "1.0.0"))
            .WithTracing(tracing => tracing
                .AddSource("Microsoft.Extensions.AI")
                .AddSource(Instrumentation.ActivitySourceName)
                .AddAzureMonitorTraceExporter(o => o.ConnectionString = appInsightsConnectionString))
            .WithMetrics(metrics => metrics
                .AddMeter("Microsoft.Extensions.AI")
                .AddMeter(Instrumentation.MeterName)
                .AddAzureMonitorMetricExporter(o => o.ConnectionString = appInsightsConnectionString));

        services.AddSingleton<IRunReportWriter>(sp =>
            new RunReportWriter(
                sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("telemetry-reports"),
                sp.GetRequiredService<IHostEnvironment>()));

        services.AddSingleton(_ =>
            new SearchClient(new Uri(config.SearchEndpoint), config.SearchIndexName, credential));
        services.AddSingleton(_ =>
            new KnowledgeBaseRetrievalClient(new Uri(config.SearchEndpoint), config.KnowledgeBaseName, credential));
        services.AddSingleton(sp =>
            new ChunkNeighborExpander(sp.GetRequiredService<SearchClient>()));
        services.AddSingleton<IRagQueryService, AgenticRagQueryService>();

        // Chunking
        services.AddSingleton<IChunkingStrategy, ChunkingStrategy1>();
        services.AddSingleton<IChunkingService, ChunkingService>();

        // CSV pipeline stages - stateless, so singletons are safe.
        services.AddSingleton<ICsvExtractor,     CsvExtractor>();
        services.AddSingleton<ICsvJoiner,        CsvJoiner>();
        services.AddSingleton<IDataCleaner,      DataCleaner>();
        services.AddSingleton<IPipelineValidator, PipelineValidator>();

        // Extraction source — exactly one IExtractionOrchestrator is active at a time. To
        // switch (e.g. to a PDF extractor), replace this registration; ExtractionService takes
        // whichever IExtractionOrchestrator is registered here, no other change needed.
        services.AddSingleton<IExtractionOrchestrator>(sp => new CsvExtractionOrchestrator(
            sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("documents"),
            sp.GetRequiredKeyedService<BlobContainerClient>("pipeline-temp"),
            sp.GetRequiredService<IRunReportWriter>(),
            sp.GetRequiredService<ICsvExtractor>(),
            sp.GetRequiredService<ICsvJoiner>(),
            sp.GetRequiredService<IDataCleaner>(),
            sp.GetRequiredService<IPipelineValidator>(),
            sp.GetRequiredService<ILogger<CsvExtractionOrchestrator>>()));

        // PDF extraction backends — comparison-only for now (see
        // docs/pdf-extraction-pipeline.md). Both registered so the
        // --compare-pdf-backends tool can resolve IEnumerable<IPdfExtractor>; neither
        // is wired into IExtractionOrchestrator yet — CSV remains the sole active source.
        services.AddSingleton<IPdfExtractor, PdfPigExtractor>();
        if (!string.IsNullOrWhiteSpace(config.DocumentIntelligenceEndpoint))
        {
            services.AddSingleton(_ =>
                new DocumentIntelligenceClient(new Uri(config.DocumentIntelligenceEndpoint), credential));
            services.AddSingleton<IPdfExtractor, DocumentIntelligenceExtractor>();
        }
        services.AddSingleton<IPdfJoiner,            PdfJoiner>();
        services.AddSingleton<IPdfCleaner,           PdfCleaner>();
        services.AddSingleton<IPdfPipelineValidator, PdfPipelineValidator>();

        // Same comparison runner the standalone --compare-pdf-backends CLI tool uses
        // (see RunPdfBackendComparisonAsync below), also registered in the main host so
        // PdfExtractionOrchestrator can trigger it as a dev-only side-run over the real
        // "documents" container instead of a developer needing a separate invocation.
        services.AddSingleton(sp => new PdfBackendComparisonRunner(
            sp.GetServices<IPdfExtractor>(),
            sp.GetRequiredService<IPdfJoiner>(),
            sp.GetRequiredService<IPdfCleaner>(),
            sp.GetRequiredService<IPdfPipelineValidator>(),
            sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("documents"),
            sp.GetRequiredService<ILogger<PdfBackendComparisonRunner>>()));

        // PDF orchestrator — registered standalone (not as IExtractionOrchestrator) since
        // CSV remains the sole active source for now; see docstring on
        // PdfExtractionOrchestrator. Explicitly picks the PdfPig backend by Name rather than
        // resolving IPdfExtractor directly, since that would silently pick whichever
        // backend was registered last once DocumentIntelligence is configured.
        services.AddSingleton(sp => new PdfExtractionOrchestrator(
            sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("documents"),
            sp.GetRequiredKeyedService<BlobContainerClient>("pipeline-temp"),
            sp.GetRequiredService<IRunReportWriter>(),
            sp.GetServices<IPdfExtractor>().Single(e => e.Name == "PdfPig"),
            sp.GetRequiredService<IPdfJoiner>(),
            sp.GetRequiredService<IPdfCleaner>(),
            sp.GetRequiredService<IPdfPipelineValidator>(),
            sp.GetRequiredService<PdfBackendComparisonRunner>(),
            sp.GetRequiredService<ILogger<PdfExtractionOrchestrator>>()));

        // RAG pipeline
        services.AddSingleton<IExtractionService, ExtractionService>();
        services.AddSingleton<IEmbeddingService, EmbeddingService>();
        services.AddSingleton<IUploadService, UploadService>();
        services.AddSingleton<IIndexService, IndexService>();
        services.AddSingleton<IIndexDocumentService, IndexDocumentService>();
        services.AddSingleton<IKnowledgeService, KnowledgeService>();
    })
    .Build();

await host.RunAsync();

// Builds a minimal, standalone DI container (not the Functions worker host) with just
// what the comparison needs, so it can run without a deployed/running Function App.
async Task RunPdfBackendComparisonAsync(string[] cliArgs)
{
    var containerName = cliArgs
        .SkipWhile(a => a != "--compare-pdf-backends")
        .Skip(1)
        .FirstOrDefault() ?? "samples";

    var rawConfig = new ConfigurationBuilder()
        .AddJsonFile("local.settings.json", optional: true)
        .AddEnvironmentVariables()
        .Build();

    // local.settings.json nests app settings under "Values"; env vars don't.
    string? GetSetting(string key) => rawConfig[$"Values:{key}"] ?? rawConfig[key];

    var storageAccountUrl = GetSetting("STORAGE_ACCOUNT_URL")
        ?? throw new InvalidOperationException(
            "STORAGE_ACCOUNT_URL not set — set it as an env var or in local.settings.json to run --compare-pdf-backends.");
    var documentIntelligenceEndpoint = GetSetting("DOCUMENT_INTELLIGENCE_ENDPOINT") ?? "";

    var compareServices = new ServiceCollection();
    TokenCredential compareCredential = new DefaultAzureCredential();

    compareServices.AddLogging(b => b.AddConsole());
    compareServices.AddSingleton(compareCredential);
    compareServices.AddSingleton(_ => new BlobServiceClient(new Uri(storageAccountUrl), compareCredential));
    compareServices.AddSingleton(sp =>
        sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient(containerName));

    compareServices.AddSingleton<IPdfExtractor, PdfPigExtractor>();
    if (!string.IsNullOrWhiteSpace(documentIntelligenceEndpoint))
    {
        compareServices.AddSingleton(_ =>
            new DocumentIntelligenceClient(new Uri(documentIntelligenceEndpoint), compareCredential));
        compareServices.AddSingleton<IPdfExtractor, DocumentIntelligenceExtractor>();
    }

    compareServices.AddSingleton<IPdfJoiner, PdfJoiner>();
    compareServices.AddSingleton<IPdfCleaner, PdfCleaner>();
    compareServices.AddSingleton<IPdfPipelineValidator, PdfPipelineValidator>();
    compareServices.AddSingleton<PdfBackendComparisonRunner>();

    await using var compareProvider = compareServices.BuildServiceProvider();
    await compareProvider.GetRequiredService<PdfBackendComparisonRunner>().CompareAsync();
}
