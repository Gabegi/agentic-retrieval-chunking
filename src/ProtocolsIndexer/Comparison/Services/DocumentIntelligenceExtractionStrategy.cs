using System.Diagnostics;
using Azure;
using Azure.AI.DocumentIntelligence;
using ProtocolsIndexer.Comparison.Interfaces;
using ProtocolsIndexer.Comparison.Models;

namespace ProtocolsIndexer.Comparison.Services;

public class DocumentIntelligenceExtractionStrategy : IExtractionStrategy
{
    public string Name => "Document Intelligence";

    private readonly DocumentIntelligenceClient _client;
    private const decimal CostPerPage = 0.001m;

    public DocumentIntelligenceExtractionStrategy(DocumentIntelligenceClient client)
    {
        _client = client;
    }

    public async Task<ExtractionResult> ExtractAsync(string blobName, byte[] pdfBytes, CancellationToken ct = default)
    {
        var sw     = Stopwatch.StartNew();
        var result = new ExtractionResult { BlobName = blobName, Method = Name };

        try
        {
            var content = new AnalyzeDocumentContent
            {
                Base64Source = BinaryData.FromBytes(pdfBytes)
            };

            var operation = await _client.AnalyzeDocumentAsync(
                WaitUntil.Completed,
                "prebuilt-layout",
                content,
                cancellationToken: ct);

            var analysis  = operation.Value;
            var pageCount = analysis.Pages?.Count ?? 0;

            DocumentChunk? current = null;

            foreach (var para in analysis.Paragraphs ?? [])
            {
                if (para.Role == DocumentParagraphRole.PageHeader
                    || para.Role == DocumentParagraphRole.PageFooter
                    || para.Role == DocumentParagraphRole.PageNumber)
                    continue;

                var pageNumber = para.BoundingRegions?.FirstOrDefault()?.PageNumber ?? 0;

                if (para.Role == DocumentParagraphRole.Title
                    || para.Role == DocumentParagraphRole.SectionHeading)
                {
                    if (current != null && !string.IsNullOrWhiteSpace(current.Content))
                        result.Chunks.Add(current);

                    current = new DocumentChunk
                    {
                        Heading    = para.Content,
                        PageNumber = pageNumber,
                        Content    = ""
                    };
                }
                else
                {
                    current ??= new DocumentChunk { PageNumber = pageNumber };
                    current.Content += (current.Content.Length > 0 ? " " : "") + para.Content;
                }
            }

            if (current != null && !string.IsNullOrWhiteSpace(current.Content))
                result.Chunks.Add(current);

            result.EstimatedCostUsd = pageCount * CostPerPage;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        result.ElapsedMs = sw.ElapsedMilliseconds;
        return result;
    }
}
