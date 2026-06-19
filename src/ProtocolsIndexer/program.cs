using Azure.AI.DocumentIntelligence;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
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

        services.AddSingleton(sp =>
            sp.GetRequiredService<BlobServiceClient>()
              .GetBlobContainerClient(config.StorageContainer));

        services.AddSingleton(_ =>
            new AzureOpenAIClient(new Uri(config.OpenAiEndpoint), credential));

        services.AddSingleton(_ =>
            new DocumentIntelligenceClient(new Uri(config.DocumentIntelligenceEndpoint), credential));

        services.AddEmbeddingGenerator(sp =>
            sp.GetRequiredService<AzureOpenAIClient>()
              .GetEmbeddingClient(config.OpenAiEmbeddingDeployment)
              .AsIEmbeddingGenerator())
            .UseOpenTelemetry();

        var appInsightsConnectionString = ctx.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        var isDev = string.IsNullOrEmpty(appInsightsConnectionString);

        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(
                serviceName:    "protocols-indexer",
                serviceVersion: "1.0.0"))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource("Microsoft.Extensions.AI")
                    .AddSource(Instrumentation.ActivitySourceName);
                if (isDev)
                    tracing.AddConsoleExporter();
                else
                    tracing.AddAzureMonitorTraceExporter(o => o.ConnectionString = appInsightsConnectionString);
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter("Microsoft.Extensions.AI")
                    .AddMeter(Instrumentation.MeterName);
                if (isDev)
                    metrics.AddConsoleExporter();
                else
                    metrics.AddAzureMonitorMetricExporter(o => o.ConnectionString = appInsightsConnectionString);
            });

        services.AddSingleton<IRequestTelemetry, RequestTelemetry>();
        services.AddSingleton<IRagQueryService, RagQueryService>();

        services.AddSingleton<IExtractionService, PdfPigExtractionService>();
        services.AddSingleton<IEmbeddingService, EmbeddingService>();
        services.AddSingleton<IIndexService, IndexService>();
        services.AddSingleton<IKnowledgeService, KnowledgeService>();
        services.AddSingleton<IPipelineOrchestrator, PipelineOrchestrator>();
    })
    .Build();

await host.RunAsync();
