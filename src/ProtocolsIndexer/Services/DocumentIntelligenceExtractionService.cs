using System.Diagnostics;
using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Storage.Blobs.Models;
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

    public async Task<ExtractionRun> ExtractAsync(BlobItem blob, byte[] pdfBytes, CancellationToken ct = default)
    {
        var sw  = Stopwatch.StartNew();
        var run = new ExtractionRun { ServiceName = Name, BlobName = blob.Name };

        try
        {
            var content   = new AnalyzeDocumentContent { Base64Source = BinaryData.FromBytes(pdfBytes) };
            var operation = await _client.AnalyzeDocumentAsync(
                WaitUntil.Completed, "prebuilt-layout", content,
                cancellationToken: ct);

            var analysis  = operation.Value;
            var pageCount = analysis.Pages?.Count ?? 0;

            // Parse metadata from full text of first two pages
            var firstPagesText = string.Join(" ",
                analysis.Paragraphs?
                    .Where(p => (p.BoundingRegions?.FirstOrDefault()?.PageNumber ?? 0) <= 2)
                    .Select(p => p.Content) ?? []);

            var meta = LciMetadataParser.Parse(firstPagesText, blob.Name);

            ProtocolDocument? current = null;
            int chunkIndex = 0;

            void FlushChunk()
            {
                if (current == null || string.IsNullOrWhiteSpace(current.Content)) return;
                run.Chunks.Add(current);
                current = null;
            }

            foreach (var para in analysis.Paragraphs ?? [])
            {
                if (para.Role == DocumentParagraphRole.PageHeader
                    || para.Role == DocumentParagraphRole.PageFooter
                    || para.Role == DocumentParagraphRole.PageNumber)
                    continue;

                var pageNum = para.BoundingRegions?.FirstOrDefault()?.PageNumber ?? 0;

                if (para.Role == DocumentParagraphRole.Title
                    || para.Role == DocumentParagraphRole.SectionHeading)
                {
                    FlushChunk();
                    current = new ProtocolDocument
                    {
                        Id              = $"{blob.Name}::{chunkIndex}",
                        SourceFile      = blob.Name,
                        RichtlijnName   = meta.RichtlijnName,
                        PublicationDate = meta.PublicationDate,
                        Version         = meta.Version,
                        Heading         = para.Content,
                        PageNumber      = pageNum,
                        ChunkIndex      = chunkIndex++,
                        Content         = ""
                    };
                }
                else
                {
                    current ??= new ProtocolDocument
                    {
                        Id              = $"{blob.Name}::{chunkIndex}",
                        SourceFile      = blob.Name,
                        RichtlijnName   = meta.RichtlijnName,
                        PublicationDate = meta.PublicationDate,
                        Version         = meta.Version,
                        PageNumber      = pageNum,
                        ChunkIndex      = chunkIndex++,
                        Content         = ""
                    };

                    current.Content += (current.Content.Length > 0 ? " " : "") + para.Content;
                }
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
