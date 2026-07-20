using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using IndexingShared.Observability.Reports;

namespace CsvIndexing;

// All of CSV's DI registrations live here, self-contained, so the Functions host
// (agentic-rag-app/Program.cs) only ever needs one line — services.AddCsvIndexing() —
// to wire the whole pipeline in. Not called from Program.cs yet: CSV has no active
// Durable flow today (see IndexingShared for the cross-cutting infra/seam types this
// assumes are already registered by the host — BlobServiceClient, TokenCredential,
// IndexerConfig, the "pipeline-temp" keyed BlobContainerClient, IRunReportWriter,
// IEmbeddingGenerator<string, Embedding<float>>).
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCsvIndexing(this IServiceCollection services)
    {
        // CSV extraction pipeline stages - stateless, so singletons are safe.
        services.AddSingleton<Services.ICsvExtractor,     Services.CsvExtractor>();
        services.AddSingleton<Services.ICsvJoiner,        Services.CsvJoiner>();
        services.AddSingleton<Services.IDataCleaner,      Services.DataCleaner>();
        services.AddSingleton<Services.IPipelineValidator, Services.PipelineValidator>();

        services.AddSingleton<Services.IExtractionOrchestrator>(sp => new Services.CsvExtractionOrchestrator(
            sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("documents"),
            sp.GetRequiredKeyedService<BlobContainerClient>("pipeline-temp"),
            sp.GetRequiredService<IRunReportWriter>(),
            sp.GetRequiredService<Services.ICsvExtractor>(),
            sp.GetRequiredService<Services.ICsvJoiner>(),
            sp.GetRequiredService<Services.IDataCleaner>(),
            sp.GetRequiredService<Services.IPipelineValidator>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Services.CsvExtractionOrchestrator>>()));

        // Chunking
        services.AddSingleton<Services.IChunkingStrategy, Services.ChunkingStrategy1>();
        services.AddSingleton<Services.IChunkingService,  Services.ChunkingService>();

        // Extraction diff / embedding / upload / index pipeline
        services.AddSingleton<Services.IExtractionService,      Services.ExtractionService>();
        services.AddSingleton<Services.IEmbeddingService,       Services.EmbeddingService>();
        services.AddSingleton<Services.IUploadService,          Services.UploadService>();
        services.AddSingleton<Services.IIndexService,           Services.IndexService>();
        services.AddSingleton<Services.IIndexDocumentService,   Services.IndexDocumentService>();

        return services;
    }
}
