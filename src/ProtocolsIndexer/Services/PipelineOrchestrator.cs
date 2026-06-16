using System.Runtime.CompilerServices;
using System.Text.Json;
using Azure.AI.OpenAI;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;
using ProtocolsIndexer.Configuration;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public class PipelineOrchestrator : IPipelineOrchestrator
{
    private readonly BlobContainerClient          _container;
    private readonly IExtractionService[]         _services;
    private readonly AzureOpenAIClient            _openAi;
    private readonly IndexerConfig                _config;
    private readonly ILogger<PipelineOrchestrator> _logger;
    private const string OutputDir = "comparison-output";

    public PipelineOrchestrator(
        BlobServiceClient               blobServiceClient,
        IEnumerable<IExtractionService> services,
        AzureOpenAIClient               openAi,
        IndexerConfig                   config,
        ILogger<PipelineOrchestrator>   logger)
    {
        _container = blobServiceClient.GetBlobContainerClient(
            string.IsNullOrEmpty(config.StorageContainer) ? "protocols" : config.StorageContainer);
        _services  = services.ToArray();
        _openAi    = openAi;
        _config    = config;
        _logger    = logger;
    }

    // ── Compare mode ─────────────────────────────────────────────────────
    public async Task CompareAsync(CancellationToken ct = default)
    {
        bool evalCoherence = Environment.GetCommandLineArgs().Contains("--eval-coherence");

        Directory.CreateDirectory(OutputDir);
        Console.WriteLine(new string('═', 88));

        var totals = _services.ToDictionary(s => s.Name, _ => new Totals());
        int count  = 0;

        await foreach (var (item, bytes) in StreamBlobsAsync(ct))
        {
            count++;
            var runs = await Task.WhenAll(_services.Select(s => s.ExtractAsync(item, bytes, ct)));

            if (evalCoherence)
                await Task.WhenAll(runs.Select(r => ScoreLlmCoherenceAsync(r, ct)));

            PrintRow(item.Name, runs);
            WriteChunks(item.Name, runs);

            foreach (var run in runs)
            {
                var t = totals[run.ServiceName];
                t.Chunks     += run.ChunkCount;
                t.Coherent   += run.CoherentChunks;
                t.Empty      += run.EmptyChunks;
                t.Oversized  += run.OversizedChunks;
                t.Undersized += run.UndersizedChunks;
                t.Headings   += run.HeadingsDetected;
                t.Fallbacks  += run.UsedFallback ? 1 : 0;
                t.Ms         += run.ElapsedMs;
                t.Cost       += run.EstimatedCostUsd;
                t.Errors     += run.Error != null ? 1 : 0;
                if (run.AvgLlmCoherence.HasValue)
                {
                    t.LlmCoherenceSum   += run.AvgLlmCoherence.Value;
                    t.LlmCoherenceCount++;
                }
            }
        }

        _logger.LogInformation("Compared {Count} PDFs across {N} services", count, _services.Length);
        PrintTotals(count, totals);
    }

    // ── LLM coherence scorer — samples up to 5 non-trivial chunks per run ─
    private async Task ScoreLlmCoherenceAsync(ExtractionRun run, CancellationToken ct)
    {
        if (run.Error != null || run.ChunkCount == 0) return;

        var sample = run.Chunks
            .Where(c => !c.IsEmpty && !c.IsUndersized)
            .OrderBy(_ => Guid.NewGuid())
            .Take(5)
            .ToList();

        if (sample.Count == 0) return;

        var chat = _openAi.GetChatClient(_config.OpenAiGptDeployment);
        var scores = new List<double>();

        foreach (var chunk in sample)
        {
            var prompt = $"""
                Rate the following Dutch medical text chunk on coherence from 1 to 5.
                5 = complete thought, starts and ends naturally, fully self-contained.
                1 = cut off mid-sentence, missing context, or incoherent fragment.
                Reply with a single integer 1-5 and nothing else.

                ---
                {chunk.Content[..Math.Min(800, chunk.Content.Length)]}
                ---
                """;

            try
            {
                var response = await chat.CompleteChatAsync(
                    [new UserChatMessage(prompt)],
                    new ChatCompletionOptions { MaxOutputTokenCount = 5 },
                    ct);

                if (int.TryParse(response.Value.Content[0].Text.Trim(), out var score) && score is >= 1 and <= 5)
                    scores.Add(score);
            }
            catch { /* non-fatal — skip this chunk */ }
        }

        if (scores.Count > 0)
            run.AvgLlmCoherence = scores.Average();
    }

    // ── Run mode ─────────────────────────────────────────────────────────
    public async Task RunAsync(CancellationToken ct = default)
    {
        if (_services.Length == 0)
            throw new InvalidOperationException("No extraction service registered.");

        var serviceName = Environment.GetEnvironmentVariable("EXTRACTION_SERVICE") ?? "pdfpig";
        var service     = _services.FirstOrDefault(s =>
                              s.Name.Contains(serviceName, StringComparison.OrdinalIgnoreCase))
                          ?? _services[0];

        _logger.LogInformation("Pipeline using {Service}", service.Name);

        await foreach (var (item, bytes) in StreamBlobsAsync(ct))
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

    // ── Streaming blob download (one PDF at a time, not all in RAM) ───────
    private async IAsyncEnumerable<(BlobItem Item, byte[] Bytes)> StreamBlobsAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var item in _container.GetBlobsAsync(cancellationToken: ct))
        {
            if (!item.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) continue;
            using var ms = new MemoryStream();
            await _container.GetBlobClient(item.Name).DownloadToAsync(ms, ct);
            yield return (item, ms.ToArray());
        }
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
        Row("Coherent chunks",    r => r.ChunkCount > 0 ? $"{r.CoherentChunks} ({100*r.CoherentChunks/r.ChunkCount}%)" : "—");
        Row("LLM coherence",      r => r.AvgLlmCoherence.HasValue ? $"{r.AvgLlmCoherence:F1}/5" : "—");
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

        TRow("Total PDFs",           _ => pdfCount.ToString());
        TRow("Total chunks",         t => t.Chunks.ToString());
        TRow("Total headings",       t => t.Headings.ToString());
        TRow("Coherent chunks",      t => t.Chunks > 0 ? $"{t.Coherent} ({100*t.Coherent/t.Chunks}%)" : "—");
        TRow("Avg LLM coherence",    t => t.LlmCoherenceCount > 0 ? $"{t.LlmCoherenceSum/t.LlmCoherenceCount:F1}/5" : "—");
        TRow("Total empty",          t => t.Empty.ToString());
        TRow("Total oversized",      t => t.Oversized.ToString());
        TRow("Total undersized",     t => t.Undersized.ToString());
        TRow("Fallback PDFs",        t => t.Fallbacks.ToString());
        TRow("Total time (ms)",      t => $"{t.Ms}ms");
        TRow("Avg time/PDF (ms)",    t => $"{(pdfCount > 0 ? t.Ms / pdfCount : 0)}ms");
        TRow("Total cost (USD)",     t => $"${t.Cost:F4}");
        TRow("Errors",               t => t.Errors.ToString());

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
        public int     Chunks            { get; set; }
        public int     Coherent          { get; set; }
        public int     Empty             { get; set; }
        public int     Oversized         { get; set; }
        public int     Undersized        { get; set; }
        public int     Headings          { get; set; }
        public int     Fallbacks         { get; set; }
        public long    Ms                { get; set; }
        public decimal Cost              { get; set; }
        public int     Errors            { get; set; }
        public double  LlmCoherenceSum   { get; set; }
        public int     LlmCoherenceCount { get; set; }
    }
}
