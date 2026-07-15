using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// Azure Document Intelligence ("prebuilt-layout") PDF extraction backend, ported
// from the comparison spike's DocumentIntelligenceExtractionService, with chunking
// stripped out entirely (chunking stays downstream, in ChunkingService, unchanged).
// Thin adapter: everything - preflight, the paid call, markdown page assembly,
// structural metadata - lives in PDFStructureExtractor.ExtractPdfStructureAsync; this
// class just maps that result onto IPdfExtractor's shared PdfFileExtraction shape.
// Produces one PdfPageRecord per PDF page with markdown-flavored content
// ("## " headings, real column-aware pipe tables), same shape CSV's
// PageRecord.PageContent arrives in.
public class DocumentIntelligenceExtractor : IPdfExtractor
{
    public string Name => "DocumentIntelligence";

    private readonly PDFStructureExtractor _structureExtractor;

    public DocumentIntelligenceExtractor(DocumentIntelligenceClient client, ILogger<DocumentIntelligenceExtractor>? logger = null)
    {
        _structureExtractor = new PDFStructureExtractor(client, logger ?? NullLogger<DocumentIntelligenceExtractor>.Instance);
    }

    public async Task<PdfFileExtraction> ExtractPDFAsync(string blobName, byte[] pdfBytes, CancellationToken ct = default)
    {
        var outcome = await _structureExtractor.ExtractPdfStructureAsync(pdfBytes, blobName, ct);
        if (!outcome.Ok)
            return new PdfFileExtraction([], null, outcome.Error);

        return new PdfFileExtraction(outcome.Pages!, outcome.Index, Error: null, EstimatedCostUsd: outcome.EstimatedCostUsd)
        {
            StructureMetadata = outcome.Metadata,
            NativeMetadata    = outcome.Metadata?.NativeMetadata,
        };
    }
}
