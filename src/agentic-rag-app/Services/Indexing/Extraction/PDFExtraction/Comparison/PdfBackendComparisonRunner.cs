using System.Diagnostics;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// Standalone comparison runner — not part of the main indexing pipeline. Runs every
// registered IPdfExtractor backend over a sample set of PDFs and pushes each one's
// output through the SAME production IPdfJoiner/IPdfCleaner/IPdfPipelineValidator the
// real orchestrator will use, so the comparison reflects actual output quality (error
// rate, reconciliation, headings/tables detected) rather than a throwaway metric.
// Replaces the earlier LLM-chunk-coherence comparison spike, which measured a
// chunking-quality question, not an extraction-quality one — chunking is decoupled
// from extraction now (see IPdfExtractor).
public class PdfBackendComparisonRunner
{
    private readonly IPdfExtractor[]        _extractors;
    private readonly IPdfJoiner             _joiner;
    private readonly IPdfCleaner            _cleaner;
    private readonly IPdfPipelineValidator  _validator;
    private readonly BlobContainerClient    _container;
    private readonly ILogger<PdfBackendComparisonRunner> _logger;

    public PdfBackendComparisonRunner(
        IEnumerable<IPdfExtractor>           extractors,
        IPdfJoiner                           joiner,
        IPdfCleaner                          cleaner,
        IPdfPipelineValidator                validator,
        BlobContainerClient                  container,
        ILogger<PdfBackendComparisonRunner>  logger)
    {
        _extractors = extractors.ToArray();
        _joiner     = joiner;
        _cleaner    = cleaner;
        _validator  = validator;
        _container  = container;
        _logger     = logger;
    }
                    /////////////////////////// PDFPig has been dropped in favour of DocumentIntelligence (see doc documentintelligence-vs-pdfpig)
    public async Task CompareAsync(CancellationToken ct = default)
    {
        var blobItems = new List<BlobItem>();
        await foreach (var item in _container.GetBlobsAsync(cancellationToken: ct))
            if (item.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                blobItems.Add(item);

        var totals = _extractors.ToDictionary(e => e.Name, _ => new Totals());
        Console.WriteLine(new string('═', 100));

        foreach (var item in blobItems)
        {
            using var ms = new MemoryStream();
            await _container.GetBlobClient(item.Name).DownloadToAsync(ms, ct);
            var bytes = ms.ToArray();

            var rows = new List<ComparisonRow>();
            foreach (var e in _extractors)
                rows.Add(await RunOneAsync(e, item.Name, bytes, ct));

            PrintRow(item.Name, rows);
            foreach (var row in rows)
                Accumulate(totals[row.BackendName], row);
        }

        _logger.LogInformation("Compared {Count} PDFs across {N} backends", blobItems.Count, _extractors.Length);
        PrintTotals(blobItems.Count, totals);
    }

    // Runs one backend over one file, then the SAME production Join -> Clean ->
    // Validate steps a real orchestrator run would use for this one file.
    private ComparisonRow RunOne(IPdfExtractor extractor, string blobName, byte[] bytes)
    {
        var sw         = Stopwatch.StartNew();
        var extraction = extractor.ExtractPDF(blobName, bytes);
        sw.Stop();

        if (extraction.Error != null)
            return new ComparisonRow(extractor.Name, sw.ElapsedMilliseconds, extraction.EstimatedCostUsd,
                Report: null, CleanResult: null, Error: extraction.Error.Message);

        var (pagesResult, indexResult) = PdfExtractionAggregation.Aggregate([extraction]);

        var joinResult   = _joiner.Join(pagesResult.Records, indexResult.Records);
        var cleanResult  = _cleaner.Clean(joinResult.Joined);
        var diagnostics  = extraction.Diagnostics is { } d ? [d] : Array.Empty<PdfExtractionDiagnostics>();
        var report       = _validator.Validate(pagesResult, indexResult, joinResult, cleanResult, previousRunCleanedCount: null, diagnostics);

        return new ComparisonRow(extractor.Name, sw.ElapsedMilliseconds, extraction.EstimatedCostUsd,
            report, cleanResult, Error: null);
    }

    private static void PrintRow(string blobName, List<ComparisonRow> rows)
    {
        Console.WriteLine($"\n📄 {blobName}");
        Console.WriteLine($"  {"Metric",-28} " + string.Join(" ", rows.Select(r => $"{r.BackendName,-26}")));
        Console.WriteLine($"  {new string('-', 28 + rows.Count * 27)}");

        void Row(string label, Func<ComparisonRow, string> val) =>
            Console.WriteLine($"  {label,-28} " +
                string.Join(" ", rows.Select(r => $"{(r.Error != null ? "ERROR" : val(r)),-26}")));

        Row("Pages",                  r => r.CleanResult!.Records.Count.ToString());
        Row("Headings detected",      r => r.CleanResult!.Records.Count(p => p.PageContent.Contains("## ")).ToString());
        Row("Table-flattening warns", r => r.Report!.Issues.Count(i => i.Stage == "TableFlattening").ToString());
        Row("Text-fidelity issues",   r => r.Report!.Issues.Count(i => i.Stage == "TextQuality").ToString());
        Row("Reconciliation probs",   r => r.Report!.ReconciliationProblems.Count.ToString());
        Row("Time (ms)",              r => $"{r.ElapsedMs}ms");
        Row("Est. cost (USD)",        r => r.CostUsd.HasValue ? $"${r.CostUsd:F4}" : "—");

        foreach (var row in rows.Where(r => r.Error != null))
            Console.WriteLine($"  ❌ {row.BackendName}: {row.Error}");
    }

    private static void Accumulate(Totals totals, ComparisonRow row)
    {
        totals.FilesAttempted++;
        totals.Ms += row.ElapsedMs;
        if (row.CostUsd is decimal cost) totals.Cost += cost;

        if (row.Error != null) { totals.Errors++; return; }

        totals.Pages             += row.CleanResult!.Records.Count;
        totals.HeadingsDetected   += row.CleanResult.Records.Count(p => p.PageContent.Contains("## "));
        totals.TableFlattening    += row.Report!.Issues.Count(i => i.Stage == "TableFlattening");
        totals.TextFidelityIssues += row.Report.Issues.Count(i => i.Stage == "TextQuality");
        totals.Reconciliation     += row.Report.ReconciliationProblems.Count;
    }

    private void PrintTotals(int pdfCount, Dictionary<string, Totals> totals)
    {
        var keys = totals.Keys.ToArray();
        var vals = totals.Values.ToArray();

        Console.WriteLine($"\n\n{new string('═', 100)}");
        Console.WriteLine("TOTALS");
        Console.WriteLine(new string('═', 100));
        Console.WriteLine($"  {"Metric",-28} " + string.Join(" ", keys.Select(k => $"{k,-26}")));
        Console.WriteLine($"  {new string('-', 28 + keys.Length * 27)}");

        void TRow(string label, Func<Totals, string> fn) =>
            Console.WriteLine($"  {label,-28} " + string.Join(" ", vals.Select(t => $"{fn(t),-26}")));

        TRow("Total PDFs",             _ => pdfCount.ToString());
        TRow("Total pages",            t => t.Pages.ToString());
        TRow("Headings detected",      t => t.HeadingsDetected.ToString());
        TRow("Table-flattening warns", t => t.TableFlattening.ToString());
        TRow("Text-fidelity issues",   t => t.TextFidelityIssues.ToString());
        TRow("Reconciliation probs",   t => t.Reconciliation.ToString());
        TRow("Total time (ms)",        t => $"{t.Ms}ms");
        TRow("Avg time/PDF (ms)",      t => $"{(pdfCount > 0 ? t.Ms / pdfCount : 0)}ms");
        TRow("Total cost (USD)",       t => $"${t.Cost:F4}");
        TRow("File errors",            t => t.Errors.ToString());

        _logger.LogInformation("PDF backend comparison complete — {Count} PDFs compared", pdfCount);
    }

    private sealed record ComparisonRow(
        string               BackendName,
        long                 ElapsedMs,
        decimal?             CostUsd,
        PdfValidationReport? Report,
        PdfCleanResult?      CleanResult,
        string?              Error);

    private sealed class Totals
    {
        public int     FilesAttempted    { get; set; }
        public int     Pages             { get; set; }
        public int     HeadingsDetected  { get; set; }
        public int     TableFlattening   { get; set; }
        public int     TextFidelityIssues{ get; set; }
        public int     Reconciliation    { get; set; }
        public long    Ms                { get; set; }
        public decimal Cost              { get; set; }
        public int     Errors            { get; set; }
    }
}
