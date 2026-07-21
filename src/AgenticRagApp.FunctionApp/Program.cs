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
using AgenticRagApp.Indexing.Pdf;
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

        // Rolling full-corpus snapshot (source-scoped, never merged across doc types) - same
        // "pipeline-artifacts" container, under its own snapshots/{source}/ path prefix.
        // Vector-cache eviction is done by the caller (IndexingFunction), not here - see
        // ISnapshotService's own comment for why.
        services.AddSingleton<ISnapshotService>(sp =>
            new SnapshotService(
                sp.GetRequiredService<IBlobStore>(),
                sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient("pipeline-artifacts"),
                sp.GetRequiredService<ILogger<SnapshotService>>()));

        // Index size telemetry + drift-check, source-scoped (see IIndexStatsMonitor) — one
        // instance shared by PDF's and CSV's own UploadService.
        services.AddSingleton<IIndexStatsMonitor, IndexStatsMonitor>();

        services.AddSingleton(sp =>
            new ChunkNeighborExpander(sp.GetRequiredService<SearchClient>()));
        services.AddSingleton<IRagQueryService, AgenticRagQueryService>();
        services.AddSingleton<IKnowledgeService, KnowledgeService>();

        // PDF indexing pipeline — extraction, chunking, embedding, upload, index. See
        // AgenticRagApp.Indexing.Pdf/ServiceCollectionExtensions.cs for what this wires in.
        services.AddPdfIndexing(config);
    })
    .Build();

await host.RunAsync();
