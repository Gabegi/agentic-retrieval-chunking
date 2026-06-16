using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Configuration;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public class PipelineOrchestrator : IPipelineOrchestrator
{
    private readonly BlobContainerClient      _container;
    private readonly IExtractionService[]     _services;
    private readonly ILogger<PipelineOrchestrator> _logger;
    private const string OutputDir = "comparison-output";

    public PipelineOrchestrator(
        BlobServiceClient            blobServiceClient,
        IEnumerable<IExtractionService> services,
        IndexerConfig                config,
        ILogger<PipelineOrchestrator> logger)
    {
        _container = blobServiceClient.GetBlobContainerClient(
            string.IsNullOrEmpty(config.StorageContainer) ? "protocols" : config.StorageContainer);
        _services  = services.ToArray();
        _logger    = logger;
    }

    // ── Compare mode ─────────────────────────────────────────────────────
    public async Task CompareAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(OutputDir);

        var blobs = await LoadBlobsAsync(ct);
        _logger.LogInformation("Comparing {Count} PDFs across {N} services", blobs.Count, _services.Length);
        Console.WriteLine($"Found {blobs.Count} PDFs\n{new string('═', 88)}");

        var totals = _services.ToDictionary(s => s.Name, _ => new Totals());

        foreach (var (item, bytes) in blobs)
        {
            var runs = await Task.WhenAll(_services.Select(s => s.ExtractAsync(item, bytes, ct)));
            PrintRow(item.Name, runs);
            WriteChunks(item.Name, runs);

            foreach (var run in runs)
            {
                var t = totals[run.ServiceName];
                t.Chunks     += run.ChunkCount;
                t.Empty      += run.EmptyChunks;
                t.Oversized  += run.OversizedChunks;
                t.Undersized += run.UndersizedChunks;
                t.Headings   += run.HeadingsDetected;
                t.Fallbacks  += run.UsedFallback ? 1 : 0;
                t.Ms         += run.ElapsedMs;
                t.Cost       += run.EstimatedCostUsd;
                t.Errors     += run.Error != null ? 1 : 0;
            }
        }

        PrintTotals(blobs.Count, totals);
    }

    // ── Run mode ─────────────────────────────────────────────────────────
    public async Task RunAsync(CancellationToken ct = default)
    {
        if (_services.Length == 0)
            throw new InvalidOperationException("No extraction service registered.");

        var service = _services[0];
        _logger.LogInformation("Pipeline starting with {Service}", service.Name);

        var blobs = await LoadBlobsAsync(ct);
        _logger.LogInformation("{Count} blobs to process", blobs.Count);

        foreach (var (item, bytes) in blobs)
        {
            var run = await service.ExtractAsync(item, bytes, ct);
            if (run.Error != null)
            {
                _logger.LogError("Extraction failed for {Blob}: {Error}", item.Name, run.Error);
                continue;
            }
            _logger.LogInformation("{Blob} → {Chunks} chunks", item.Name, run.ChunkCount);

            // TODO: embed and index run.Chunks once winning service is chosen
        }
    }

    // ── Blob loading ─────────────────────────────────────────────────────
    private async Task<List<(BlobItem Item, byte[] Bytes)>> LoadBlobsAsync(CancellationToken ct)
    {
        var blobs = new List<(BlobItem, byte[])>();
        await foreach (var item in _container.GetBlobsAsync(cancellationToken: ct))
        {
            if (!item.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) continue;
            using var ms = new MemoryStream();
            await _container.GetBlobClient(item.Name).DownloadToAsync(ms, ct);
            blobs.Add((item, ms.ToArray()));
        }
        return blobs;
    }

    // ── Console output ────────────────────────────────────────────────────
    private void PrintRow(string blobName, ExtractionRun[] runs)
    {
        Console.WriteLine($"\n📄 {blobName}");
        Console.WriteLine($"  {"Metric",-28} " + string.Join(" ", runs.Select(r => $"{r.ServiceName,-26}")));
        Console.WriteLine($"  {new string('-', 28 + runs.Length * 27)}");

        void Row(string label, Func<ExtractionRun, string> val) =>
            Console.WriteLine($"  {label,-28} " +
                string.Join(" ", runs.Select(r => $"{(r.Error != null ? "ERROR" : val(r)),-26}")));

        Row("Chunks",             r => r.ChunkCount.ToString());
        Row("Avg tokens",         r => $"{r.AvgTokens:F0}");
        Row("Headings detected",  r => r.HeadingsDetected.ToString());
        Row("Empty chunks",       r => r.EmptyChunks.ToString());
        Row("Oversized (>1024t)", r => r.OversizedChunks.ToString());
        Row("Undersized (<20t)",  r => r.UndersizedChunks.ToString());
        Row("Fallback used",      r => r.UsedFallback ? "YES ⚠️" : "No");
        Row("Time (ms)",          r => $"{r.ElapsedMs}ms");
        Row("Est. cost (USD)",    r => $"${r.EstimatedCostUsd:F4}");

        foreach (var run in runs.Where(r => r.Error != null))
            Console.WriteLine($"  ❌ {run.ServiceName}: {run.Error}");
    }

    private void PrintTotals(int pdfCount, Dictionary<string, Totals> totals)
    {
        var keys = totals.Keys.ToArray();
        var vals = totals.Values.ToArray();

        Console.WriteLine($"\n\n{new string('═', 88)}");
        Console.WriteLine("TOTALS");
        Console.WriteLine(new string('═', 88));
        Console.WriteLine($"  {"Metric",-28} " + string.Join(" ", keys.Select(k => $"{k,-26}")));
        Console.WriteLine($"  {new string('-', 28 + keys.Length * 27)}");

        void TRow(string label, Func<Totals, string> fn) =>
            Console.WriteLine($"  {label,-28} " + string.Join(" ", vals.Select(t => $"{fn(t),-26}")));

        TRow("Total PDFs",        _ => pdfCount.ToString());
        TRow("Total chunks",      t => t.Chunks.ToString());
        TRow("Total headings",    t => t.Headings.ToString());
        TRow("Total empty",       t => t.Empty.ToString());
        TRow("Total oversized",   t => t.Oversized.ToString());
        TRow("Total undersized",  t => t.Undersized.ToString());
        TRow("Fallback PDFs",     t => t.Fallbacks.ToString());
        TRow("Total time (ms)",   t => $"{t.Ms}ms");
        TRow("Avg time/PDF (ms)", t => $"{(pdfCount > 0 ? t.Ms / pdfCount : 0)}ms");
        TRow("Total cost (USD)",  t => $"${t.Cost:F4}");
        TRow("Errors",            t => t.Errors.ToString());

        Console.WriteLine($"\n✅ Done\n📁 Chunks written to ./{OutputDir}/");
    }

    // ── Chunk file output ─────────────────────────────────────────────────
    private static void WriteChunks(string blobName, ExtractionRun[] runs)
    {
        var slug = blobName.Replace('/', '_').Replace(".pdf", "");
        var dir  = Path.Combine(OutputDir, slug);
        Directory.CreateDirectory(dir);

        foreach (var run in runs)
        {
            var fileName = run.ServiceName.Replace(" ", "-").ToLowerInvariant() + ".txt";
            using var w  = new StreamWriter(Path.Combine(dir, fileName), append: false);

            w.WriteLine($"Method : {run.ServiceName}");
            w.WriteLine($"Blob   : {run.BlobName}");
            w.WriteLine($"Chunks : {run.ChunkCount}  |  Time: {run.ElapsedMs}ms  |  Cost: ${run.EstimatedCostUsd:F4}");

            if (run.Error != null) { w.WriteLine($"ERROR  : {run.Error}"); continue; }

            w.WriteLine(new string('─', 80));

            foreach (var chunk in run.Chunks)
            {
                w.WriteLine($"\n── Chunk {chunk.ChunkIndex} (page {chunk.PageNumber}, ~{chunk.TokenEstimate} tokens)");
                if (chunk.Heading != null) w.WriteLine($"## {chunk.Heading}");
                w.WriteLine(chunk.Content);
            }
        }
    }

    private sealed class Totals
    {
        public int     Chunks    { get; set; }
        public int     Empty     { get; set; }
        public int     Oversized { get; set; }
        public int     Undersized{ get; set; }
        public int     Headings  { get; set; }
        public int     Fallbacks { get; set; }
        public long    Ms        { get; set; }
        public decimal Cost      { get; set; }
        public int     Errors    { get; set; }
    }
}
