using Azure.Search.Documents;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AgenticRagApp.Infrastructure;
using AgenticRagApp.Infrastructure.Clients.Blob;
using AgenticRagApp.Observability;
using AgenticRagApp.Observability.Reports;
using AgenticRagApp.Services;
using System.Text;

// Required for PdfCleaner's Windows-1252 mojibake repair (Encoding.GetEncoding(1252)) -
// code pages beyond the built-in set aren't available on .NET Core+ without this.
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((ctx, services) =>
    {
        // Config validation, IndexerConfig, credential, and every Azure SDK client this
        // app talks to (Blob, Search, OpenAI, Document Intelligence) are registered here.
        var config = services.AddAgenticRagAppInfrastructure(ctx.Configuration);

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
                sp.GetRequiredService<IBlobStore>(),
                sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("pipeline-reports"),
                sp.GetRequiredService<IHostEnvironment>()));

        // Persistent full-content archive - separate container from pipeline-reports above,
        // always on (not gated by environment or a config flag - see IPipelineArtifactWriter).
        services.AddSingleton<IPipelineArtifactWriter>(sp =>
            new PipelineArtifactWriter(
                sp.GetRequiredService<IBlobStore>(),
                sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("pipeline-artifacts")));

        // Content-hash-keyed embedding vector cache - same "pipeline-artifacts" container as
        // the archive above, under its own vector-cache/ path prefix (see VectorCache).
        services.AddSingleton<IVectorCache>(sp =>
            new VectorCache(
                sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("pipeline-artifacts")));

        // Rolling full-corpus snapshot (source-scoped, never merged across doc types) - same
        // "pipeline-artifacts" container, under its own snapshots/{source}/ path prefix.
        // Vector-cache eviction is done by the caller (IndexingFunction), not here - see
        // ISnapshotService's own comment for why.
        services.AddSingleton<ISnapshotService>(sp =>
            new SnapshotService(
                sp.GetRequiredService<IBlobStore>(),
                sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("pipeline-artifacts"),
                sp.GetRequiredService<ILogger<SnapshotService>>()));

        services.AddSingleton(sp =>
            new ChunkNeighborExpander(sp.GetRequiredService<SearchClient>()));
        services.AddSingleton<IRagQueryService, AgenticRagQueryService>();

        // Chunking
        services.AddSingleton<IChunkingStrategy, PdfChunkingStrategy1>();
        services.AddSingleton<IChunkingService, ChunkingService>();

        // PDF extraction backend — only registered when Document Intelligence is
        // configured (Infrastructure only registers the DocumentIntelligenceClient itself
        // under the same condition); PdfExtractionOrchestrator resolves it explicitly by
        // Name below rather than via GetRequiredService, so a future second backend can't
        // silently change which one gets picked.
        if (!string.IsNullOrWhiteSpace(config.DocumentIntelligenceEndpoint))
        {
            services.AddSingleton<PdfDocumentAnalyzer>();
            services.AddSingleton<IPdfExtractor, DocumentIntelligenceExtractor>();
        }
        services.AddSingleton<IPdfCleaner,           PdfCleaner>();
        services.AddSingleton<IPdfPipelineValidator, PdfPipelineValidator>();

        // Extraction source — PDF is the only IExtractionOrchestrator left in this project.
        // CSV's pipeline (extractor, chunking, embedding, upload, index) moved out to the
        // standalone CsvIndexing project — see IndexingShared for the source-agnostic
        // seam types (ExtractionDocument/ExtractionOutput/etc.) both sides return/consume.
        services.AddSingleton<IExtractionOrchestrator>(sp => new PdfExtractionOrchestrator(
            sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("documents"),
            sp.GetRequiredKeyedService<BlobContainerClient>("pipeline-temp"),
            sp.GetRequiredService<IBlobStore>(),
            sp.GetRequiredService<IRunReportWriter>(),
            sp.GetServices<IPdfExtractor>().Single(e => e.Name == "DocumentIntelligence"),
            sp.GetRequiredService<IPdfCleaner>(),
            sp.GetRequiredService<IPdfPipelineValidator>(),
            sp.GetRequiredService<IHostEnvironment>(),
            sp.GetRequiredService<ILogger<PdfExtractionOrchestrator>>()));

        // RAG pipeline
        services.AddSingleton<IExtractionService>(sp => new ExtractionService(
            sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("documents"),
            sp.GetRequiredService<IBlobStore>(),
            sp.GetRequiredService<IExtractionOrchestrator>(),
            sp.GetRequiredService<IIndexDocumentService>(),
            sp.GetRequiredService<IRunReportWriter>(),
            sp.GetRequiredService<ILogger<ExtractionService>>()));
        services.AddSingleton<IEmbeddingService, EmbeddingService>();
        services.AddSingleton<IUploadService, UploadService>();
        services.AddSingleton<IIndexService, IndexService>();
        services.AddSingleton<IIndexDocumentService, IndexDocumentService>();
        services.AddSingleton<IKnowledgeService, KnowledgeService>();
    })
    .Build();

await host.RunAsync();
