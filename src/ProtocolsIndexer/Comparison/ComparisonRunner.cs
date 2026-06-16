using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Comparison.Interfaces;
using ProtocolsIndexer.Comparison.Models;
using ProtocolsIndexer.Comparison.Services;

namespace ProtocolsIndexer.Comparison;

public class ComparisonRunner
{
    private readonly BlobContainerClient      _container;
    private readonly IExtractionStrategy[]    _strategies;
    private readonly ILogger<ComparisonRunner> _logger;
    private const string OutputDir = "comparison-output";

    public ComparisonRunner(
        BlobServiceClient blobServiceClient,
        PdfPigExtractionStrategy pdfPig,
        DocumentIntelligenceExtractionStrategy docInt,
        ILogger<ComparisonRunner> logger)
    {
        _container  = blobServiceClient.GetBlobContainerClient("protocols");
        _strategies = [pdfPig, docInt];
        _logger     = logger;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(OutputDir);

        Console.WriteLine("Loading PDFs from 'protocols' container...\n");
        var blobs = new List<(string Name, byte[] Bytes)>();

        await foreach (var item in _container.GetBlobsAsync(cancellationToken: ct))
        {
            if (!item.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) continue;

            using var ms     = new MemoryStream();
            var       client = _container.GetBlobClient(item.Name);
            await client.DownloadToAsync(ms, ct);
            blobs.Add((item.Name, ms.ToArray()));
        }

        Console.WriteLine($"Found {blobs.Count} PDFs");
        Console.WriteLine(new string('═', 88));

        var stats = _strategies.ToDictionary(s => s.Name, _ => (
            TotalMs: 0L, TotalCost: 0m, TotalChunks: 0,
            TotalEmpty: 0, TotalShort: 0, Errors: 0));

        foreach (var (name, bytes) in blobs)
            await ProcessBlobAsync(name, bytes, stats, ct);

        PrintTotals(stats, blobs.Count);
    }

    private async Task ProcessBlobAsync(
        string name,
        byte[] bytes,
        Dictionary<string, (long TotalMs, decimal TotalCost, int TotalChunks, int TotalEmpty, int TotalShort, int Errors)> stats,
        CancellationToken ct)
    {
        Console.WriteLine($"\n📄 {name}");
        Console.WriteLine($"  {"Metric",-28} {_strategies[0].Name,-26} {_strategies[1].Name,-26}");
        Console.WriteLine($"  {new string('-', 82)}");

        var results = await Task.WhenAll(_strategies.Select(s => s.ExtractAsync(name, bytes, ct)));

        void Row(string label, Func<ExtractionResult, string> value) =>
            Console.WriteLine($"  {label,-28} {value(results[0]),-26} {value(results[1]),-26}");

        Row("Chunks produced",     r => r.Error != null ? "ERROR"               : r.ChunkCount.ToString());
        Row("Avg chunk tokens",    r => r.Error != null ? "-"                   : $"{r.AvgChunkTokens:F0}");
        Row("Sections detected",   r => r.Error != null ? "-"                   : r.SectionsDetected.ToString());
        Row("Empty chunks",        r => r.Error != null ? "-"                   : r.EmptyChunks.ToString());
        Row("Short chunks (<20t)", r => r.Error != null ? "-"                   : r.ShortChunks.ToString());
        Row("Time (ms)",           r => r.Error != null ? "-"                   : $"{r.ElapsedMs}ms");
        Row("Est. cost (USD)",     r => r.Error != null ? "-"                   : $"${r.EstimatedCostUsd:F4}");

        if (results.Any(r => r.Error != null))
            Console.WriteLine($"  ⚠️  {string.Join(" | ", results.Select(r => $"{r.Method}: {r.Error ?? "ok"}"))}");

        var slug = name.Replace('/', '_').Replace(".pdf", "");
        var dir  = Path.Combine(OutputDir, slug);
        Directory.CreateDirectory(dir);

        for (int i = 0; i < _strategies.Length; i++)
            await WriteChunksAsync(Path.Combine(dir, $"{_strategies[i].Name.Replace(" ", "-").ToLowerInvariant()}.txt"), results[i]);

        for (int i = 0; i < _strategies.Length; i++)
        {
            var r  = results[i];
            var s  = _strategies[i].Name;
            var st = stats[s];
            stats[s] = (
                st.TotalMs     + r.ElapsedMs,
                st.TotalCost   + r.EstimatedCostUsd,
                st.TotalChunks + r.ChunkCount,
                st.TotalEmpty  + r.EmptyChunks,
                st.TotalShort  + r.ShortChunks,
                st.Errors      + (r.Error != null ? 1 : 0));
        }
    }

    private static async Task WriteChunksAsync(string path, ExtractionResult result)
    {
        await using var w = new StreamWriter(path, append: false);
        await w.WriteLineAsync($"Method : {result.Method}");
        await w.WriteLineAsync($"Blob   : {result.BlobName}");
        await w.WriteLineAsync($"Chunks : {result.ChunkCount}  |  Time: {result.ElapsedMs}ms  |  Cost: ${result.EstimatedCostUsd:F4}");

        if (result.Error != null)
        {
            await w.WriteLineAsync($"ERROR  : {result.Error}");
            return;
        }

        await w.WriteLineAsync(new string('─', 80));

        foreach (var (chunk, i) in result.Chunks.Select((c, i) => (c, i + 1)))
        {
            await w.WriteLineAsync($"\n── Chunk {i} (page {chunk.PageNumber}, ~{chunk.TokenEstimate} tokens)");
            if (chunk.Heading != null)
                await w.WriteLineAsync($"## {chunk.Heading}");
            await w.WriteLineAsync(chunk.Content);
        }
    }

    private void PrintTotals(
        Dictionary<string, (long TotalMs, decimal TotalCost, int TotalChunks, int TotalEmpty, int TotalShort, int Errors)> stats,
        int pdfCount)
    {
        var vals = stats.Values.ToArray();

        Console.WriteLine($"\n\n{new string('═', 88)}");
        Console.WriteLine("TOTALS");
        Console.WriteLine(new string('═', 88));
        Console.WriteLine($"  {"Metric",-28} {_strategies[0].Name,-26} {_strategies[1].Name,-26}");
        Console.WriteLine($"  {new string('-', 82)}");

        void TRow(string label, Func<(long TotalMs, decimal TotalCost, int TotalChunks, int TotalEmpty, int TotalShort, int Errors), string> fn) =>
            Console.WriteLine($"  {label,-28} {fn(vals[0]),-26} {fn(vals[1]),-26}");

        TRow("Total chunks",       s => s.TotalChunks.ToString());
        TRow("Total empty chunks", s => s.TotalEmpty.ToString());
        TRow("Total short chunks", s => s.TotalShort.ToString());
        TRow("Total time (ms)",    s => $"{s.TotalMs}ms");
        TRow("Avg time/PDF (ms)",  s => $"{(pdfCount > 0 ? s.TotalMs / pdfCount : 0)}ms");
        TRow("Total cost (USD)",   s => $"${s.TotalCost:F4}");
        TRow("Errors",             s => s.Errors.ToString());

        Console.WriteLine($"\n✅ Done — {pdfCount} PDFs compared");
        Console.WriteLine($"📁 Chunks written to ./{OutputDir}/");
    }
}
