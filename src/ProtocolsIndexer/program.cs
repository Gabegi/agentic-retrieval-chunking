using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using InvoiceIndexer.Configuration;
using InvoiceIndexer.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Azure;
using System.ClientModel;
using Microsoft.Extensions.Resilience;
using Polly;
using Polly.Retry;
using Polly.Registry;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureLogging(log =>
    {
        log.ClearProviders();
        log.AddConsole();
        log.SetMinimumLevel(LogLevel.Information);
    })
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
            StorageAccountUrl            = ctx.Configuration["STORAGE_ACCOUNT_URL"]!,
            StorageContainer             = ctx.Configuration["STORAGE_CONTAINER"]!,
            SearchIndexName              = ctx.Configuration["SEARCH_INDEX_NAME"]!,
            KnowledgeSourceName          = ctx.Configuration["KNOWLEDGE_SOURCE_NAME"]!,
            KnowledgeBaseName            = ctx.Configuration["KNOWLEDGE_BASE_NAME"]!,
        };

        TokenCredential credential = new DefaultAzureCredential();

        services.AddSingleton(config);
        services.AddSingleton(credential);

        // Azure clients
        services.AddSingleton(_ =>
            new BlobServiceClient(new Uri(config.StorageAccountUrl), credential));

        services.AddSingleton(_ =>
            new AzureOpenAIClient(new Uri(config.OpenAiEndpoint), credential));

        // Resilience — register pipeline via Microsoft.Extensions.Resilience, then expose it as a typed singleton
        services.AddResiliencePipeline("openai", builder =>
        {
            builder
                .AddRetry(new RetryStrategyOptions
                {
                    MaxRetryAttempts = 5,
                    Delay            = TimeSpan.FromSeconds(10),
                    BackoffType      = DelayBackoffType.Exponential,
                    ShouldHandle     = new PredicateBuilder()
                        .Handle<ClientResultException>(e =>
                            e.Message.Contains("429"))
                })
                .AddTimeout(TimeSpan.FromSeconds(60));
        });
        services.AddSingleton(sp =>
            sp.GetRequiredService<ResiliencePipelineProvider<string>>()
              .GetPipeline("openai"));

        // Services
        services.AddSingleton<IIndexService, IndexService>();
        services.AddSingleton<IDocumentService, DocumentService>();
        services.AddSingleton<IEmbeddingService, EmbeddingService>();
        services.AddSingleton<IKnowledgeService, KnowledgeService>();
        services.AddSingleton<IPipelineOrchestrator, PipelineOrchestrator>();
    })
    .Build();

// run the pipeline
var orchestrator = host.Services.GetRequiredService<IPipelineOrchestrator>();
await orchestrator.RunAsync();