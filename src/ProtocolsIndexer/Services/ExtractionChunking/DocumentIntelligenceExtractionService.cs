using System.Diagnostics;
using System.Text;
using Azure;
using Azure.AI.DocumentIntelligence;
using ProtocolsIndexer.Models;
using ProtocolsIndexer.Utils;

namespace ProtocolsIndexer.Services;

public class DocumentIntelligenceExtractionService : IExtractionService
{
    public string Name => "Document Intelligence";

    private readonly DocumentIntelligenceClient _client;
    private const decimal CostPerPage = 0.001m;

    public DocumentIntelligenceExtractionService(DocumentIntelligenceClient client)
    {
        _client = client;
    }

    public async Task<ExtractionRun> ExtractAsync(string blobName, byte[] pdfBytes, CancellationToken ct = default)
    {
        var sw  = Stopwatch.StartNew();
        var run = new ExtractionRun { ServiceName = Name, BlobName = blobName };

        try
        {
            var operation = await _client.AnalyzeDocumentAsync(
                WaitUntil.Completed, "prebuilt-layout", BinaryData.FromBytes(pdfBytes),
                cancellationToken: ct);

            var analysis  = operation.Value;
            var pageCount = analysis.Pages?.Count ?? 0;

            // Preserve newlines so multiline regexes in LciMetadataParser anchor correctly
            var firstPagesText = string.Join("\n",
                analysis.Paragraphs?
                    .Where(p => (p.BoundingRegions is { Count: > 0 } br0 ? br0[0].PageNumber : 0) <= 2)
                    .Select(p => p.Content) ?? []);

            var meta       = LciMetadataParser.Parse(firstPagesText, blobName);
            int chunkIndex = 0;
            ProtocolDocument? current = null;

            // ChunkIndex assigned on flush — not on body-accumulator creation
            void FlushChunk()
            {
                if (current == null
                    || string.IsNullOrWhiteSpace(current.Content)
                    || current.Content.Split(' ').Length < 5) return;

                foreach (var part in SplitContent(current.Content))
                {
                    run.Chunks.Add(new ProtocolDocument
                    {
                        Id              = SafeKey(blobName, chunkIndex),
                        SourceFile      = current.SourceFile,
                        RichtlijnName   = current.RichtlijnName,
                        PublicationDate = current.PublicationDate,
                        Version         = current.Version,
                        PageNumber      = current.PageNumber,
                        Heading         = current.Heading,
                        Content         = part,
                        ChunkIndex      = chunkIndex++
                    });
                }
                current = null;
            }

            ProtocolDocument BaseDoc(int pageNum) => new()
            {
                Id              = "",
                SourceFile      = blobName,
                RichtlijnName   = meta.RichtlijnName,
                PublicationDate = meta.PublicationDate,
                Version         = meta.Version,
                PageNumber      = pageNum,
                Content         = ""
            };

            foreach (var para in analysis.Paragraphs ?? [])
            {
                if (para.Role == ParagraphRole.PageHeader
                    || para.Role == ParagraphRole.PageFooter
                    || para.Role == ParagraphRole.PageNumber)
                    continue;

                var pageNum = para.BoundingRegions is { Count: > 0 } brP ? brP[0].PageNumber : 0;

                if (para.Role == ParagraphRole.Title
                    || para.Role == ParagraphRole.SectionHeading)
                {
                    FlushChunk();
                    current         = BaseDoc(pageNum);
                    current.Heading = para.Content;
                }
                else
                {
                    current ??= BaseDoc(pageNum);
                    current.Content += (current.Content.Length > 0 ? " " : "") + para.Content;
                }
            }

            // Tables: append to current chunk (dosage tables, diagnostic criteria, etc.)
            foreach (var table in analysis.Tables ?? [])
            {
                var pageNum   = table.BoundingRegions is { Count: > 0 } brT ? brT[0].PageNumber : 0;
                var tableText = string.Join(" | ", table.Cells
                    .OrderBy(c => c.RowIndex).ThenBy(c => c.ColumnIndex)
                    .Select(c => c.Content.Trim())
                    .Where(c => !string.IsNullOrWhiteSpace(c)));

                if (string.IsNullOrWhiteSpace(tableText)) continue;

                current ??= BaseDoc(pageNum);
                current.Content += (current.Content.Length > 0 ? " " : "") + tableText;
            }

            FlushChunk();

            run.EstimatedCostUsd = pageCount * CostPerPage;
        }
        catch (Exception ex)
        {
            run.Error = ex.Message;
        }

        run.ElapsedMs = sw.ElapsedMilliseconds;
        return run;
    }
}
