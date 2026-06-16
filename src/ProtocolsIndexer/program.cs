using Azure.AI.DocumentIntelligence;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using ProtocolsIndexer.Comparison;
using ProtocolsIndexer.Comparison.Services;
using ProtocolsIndexer.Configuration;
using ProtocolsIndexer.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
            SearchEndpoint             = ctx.Configuration["SEARCH_ENDPOINT"]!,
            OpenAiEndpoint             = ctx.Configuration["OPENAI_ENDPOINT"]!,
            OpenAiEmbeddingDeployment  = ctx.Configuration["OPENAI_EMBEDDING_DEPLOYMENT"]!,
            OpenAiGptDeployment        = ctx.Configuration["OPENAI_GPT_DEPLOYMENT"]!,
            OpenAiGptModelName         = ctx.Configuration["OPENAI_GPT_MODEL_NAME"]!,
            OpenAiExtractionDeployment    = ctx.Configuration["OPENAI_EXTRACTION_DEPLOYMENT"] ?? "gpt-41-extraction",
            DocumentIntelligenceEndpoint  = ctx.Configuration["DOCUMENT_INTELLIGENCE_ENDPOINT"] ?? "",
            StorageAccountUrl             = ctx.Configuration["STORAGE_ACCOUNT_URL"]!,
            StorageContainer           = ctx.Configuration["STORAGE_CONTAINER"]!,
            SearchIndexName            = ctx.Configuration["SEARCH_INDEX_NAME"]!,
            KnowledgeSourceName        = ctx.Configuration["KNOWLEDGE_SOURCE_NAME"]!,
            KnowledgeBaseName          = ctx.Configuration["KNOWLEDGE_BASE_NAME"]!,
        };

        TokenCredential credential = new DefaultAzureCredential();

        services.AddSingleton(config);
        services.AddSingleton(credential);

        services.AddSingleton(_ =>
            new BlobServiceClient(new Uri(config.StorageAccountUrl), credential));

        services.AddSingleton(_ =>
            new AzureOpenAIClient(new Uri(config.OpenAiEndpoint), credential));

        services.AddSingleton<IIndexService, IndexService>();
        services.AddSingleton<IExtractionService, ExtractionService>();
        services.AddSingleton<IEmbeddingService, EmbeddingService>();
        services.AddSingleton<IKnowledgeService, KnowledgeService>();
        services.AddSingleton<IPipelineOrchestrator, PipelineOrchestrator>();

        if (!string.IsNullOrEmpty(config.DocumentIntelligenceEndpoint))
        {
            services.AddSingleton(_ =>
                new DocumentIntelligenceClient(new Uri(config.DocumentIntelligenceEndpoint), credential));
            services.AddSingleton<PdfPigExtractionStrategy>();
            services.AddSingleton<DocumentIntelligenceExtractionStrategy>();
            services.AddSingleton<ComparisonRunner>();
        }
    })
    .Build();

if (args.Contains("--compare"))
{
    var runner = host.Services.GetRequiredService<ComparisonRunner>();
    await runner.RunAsync();
    return;
}

var orchestrator = host.Services.GetRequiredService<IPipelineOrchestrator>();
await orchestrator.RunAsync();
