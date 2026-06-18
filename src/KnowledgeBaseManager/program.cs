using Azure.Core;
using Azure.Identity;
using KnowledgeBaseManager.Configuration;
using KnowledgeBaseManager.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((ctx, services) =>
    {
        var config = new KnowledgeBaseConfig
        {
            SearchEndpoint      = ctx.Configuration["SEARCH_ENDPOINT"]!,
            OpenAiEndpoint      = ctx.Configuration["OPENAI_ENDPOINT"]!,
            OpenAiGptDeployment = ctx.Configuration["OPENAI_GPT_DEPLOYMENT"]!,
            OpenAiGptModelName  = ctx.Configuration["OPENAI_GPT_MODEL_NAME"]!,
            SearchIndexName     = ctx.Configuration["SEARCH_INDEX_NAME"]!,
            KnowledgeSourceName = ctx.Configuration["KNOWLEDGE_SOURCE_NAME"]!,
            KnowledgeBaseName   = ctx.Configuration["KNOWLEDGE_BASE_NAME"]!,
        };

        services.AddSingleton(config);
        services.AddSingleton<TokenCredential>(_ => new DefaultAzureCredential());
        services.AddSingleton<IKnowledgeService, KnowledgeService>();
    })
    .Build();

await host.RunAsync();
