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
