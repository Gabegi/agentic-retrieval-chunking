using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// Azure Document Intelligence ("prebuilt-layout") PDF extraction backend, ported
// from the comparison spike's DocumentIntelligenceExtractionService, with chunking
// stripped out entirely (chunking stays downstream, in ChunkingService, unchanged).
// Owns the PdfDocument's lifetime up front: preflight (PdfDocumentValidator.IsPDFValid)
// opens and validates it, then PdfMetadataExtractor.ParseNativeMetadata takes over that
// lifetime - it reads native metadata/bookmarks off the PdfDocument and disposes it
// before returning, so nothing here needs its own `using` block. The resulting
// DocMetadata is handed to PDFStructureExtractor.ExtractPdfStructureAsync, which does
// the paid call, markdown page assembly, and structural metadata from there.
// Produces one PdfPageRecord per PDF page with markdown-flavored content
// ("## " headings, real column-aware pipe tables), same shape CSV's
// PageRecord.PageContent arrives in.
public class DocumentIntelligenceExtractor : IPdfExtractor
{
    public string Name => "DocumentIntelligence";

    private readonly ILogger<DocumentIntelligenceExtractor> _logger;
    private readonly PDFStructureExtractor                   _structureExtractor;

    public DocumentIntelligenceExtractor(PDFStructureExtractor structureExtractor, ILogger<DocumentIntelligenceExtractor>? logger = null)
    {
        _logger             = logger ?? NullLogger<DocumentIntelligenceExtractor>.Instance;
        _structureExtractor = structureExtractor;
    }

    public async Task<PdfFileExtraction> ExtractPDFAsync(string blobName, byte[] pdfBytes, CancellationToken ct = default)
    {
        // Step 1: local, free structural check — rejects oversized/corrupt/encrypted/
        // too-many-page files before spending a paid Document Intelligence call on them.
        if (!PdfDocumentValidator.IsPDFValid(pdfBytes, blobName, _logger, out var pdf, out var validationError))
            return new PdfFileExtraction([], validationError);

        // Step 2: ParseNativeMetadata takes ownership of pdf's lifetime (disposes it internally)
        // and reads everything PdfPig can offer beyond DI: native Title/Author/
        // CreationDate plus the outline/bookmark tree.
        var nativeMetadata = PdfNativeMetadataExtractor.ExtractPdfNativeMetadata(pdf, blobName, _logger);

        // Step 3: submit to Document Intelligence's prebuilt-layout model and assemble
        // pages/structural metadata — lives in PDFStructureExtractor.
        var outcome = await _structureExtractor.ExtractPdfStructureAsync(pdfBytes, blobName, nativeMetadata, ct);
        if (!outcome.Ok)
            return new PdfFileExtraction([], outcome.Error);

        return new PdfFileExtraction(outcome.Pages!, Error: null, EstimatedCostUsd: outcome.EstimatedCostUsd)
        {
            StructureMetadata = outcome.Metadata,
            NativeMetadata    = nativeMetadata,
        };
    }
}
