using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Azure.Search.Documents;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
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
using ProtocolsIndexer.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((ctx, services) =>
    {
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
            .UseOpenTelemetry();

        services.AddChatClient(sp =>
            sp.GetRequiredService<AzureOpenAIClient>()
              .GetChatClient(config.OpenAiGptDeployment)
              .AsIChatClient())
            .UseOpenTelemetry();

        var appInsightsConnectionString = ctx.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]!;

        services.AddOpenTelemetry()
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

        services.AddSingleton<IRequestTelemetry, RequestTelemetry>();
        services.AddSingleton(_ =>
            new SearchClient(new Uri(config.SearchEndpoint), config.SearchIndexName, credential));
        services.AddSingleton<IRagQueryService, RagQueryService>();

        // Chunking
        services.AddSingleton<IChunkingStrategy, ChunkingStrategy1>();
        services.AddSingleton<IChunkingService, ChunkingService>();

        // Extractors — add new IExtractionOrchestrator implementations here to support new sources
        services.AddSingleton<IExtractionOrchestrator>(sp => new CsvExtractionOrchestrator(
            sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("documentscsv"),
            sp.GetRequiredService<ILogger<CsvExtractionOrchestrator>>()));

        // RAG pipeline
        services.AddSingleton<IEmbeddingService, EmbeddingService>();
        services.AddSingleton<IIndexService, IndexService>();
        services.AddSingleton<IIndexDocumentService, IndexDocumentService>();
        services.AddSingleton<IKnowledgeService, KnowledgeService>();
        services.AddSingleton<IIndexingPipelineOrchestrator, IndexingPipelineOrchestrator>();
    })
    .Build();

await host.RunAsync();
