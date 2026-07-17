using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// Azure Document Intelligence ("prebuilt-layout") PDF extraction backend, ported
// from the comparison spike's DocumentIntelligenceExtractionService, with chunking
// stripped out entirely (chunking stays downstream, in ChunkingService, unchanged).
// Owns the PdfDocument's lifetime up front: preflight (PdfDocumentValidator.IsPDFValid)
// opens and validates it, then PdfNativeMetadataExtractor.ExtractPdfNativeMetadata takes
// over that lifetime - it reads native metadata/bookmarks off the PdfDocument and disposes it
// before returning, so nothing here needs its own `using` block. The resulting
// DocMetadata is handed to PDFDocumentAnalyzer.AnalyzeDocumentAsync, which does
// the paid call, markdown page assembly, and structural extraction from there.
// This class is the assembler: it combines what each of the three steps produced into
// one PDFExtractionResult - the complete record of everything the pipeline learned about
// this PDF - rather than each step's output getting scattered/lost along the way.
public class DocumentIntelligenceExtractor : IPdfExtractor
{
    public string Name => "DocumentIntelligence";

    private readonly ILogger<DocumentIntelligenceExtractor> _logger;
    private readonly PDFDocumentAnalyzer                      _structureExtractor;

    public DocumentIntelligenceExtractor(PDFDocumentAnalyzer structureExtractor, ILogger<DocumentIntelligenceExtractor>? logger = null)
    {
        _logger             = logger ?? NullLogger<DocumentIntelligenceExtractor>.Instance;
        _structureExtractor = structureExtractor;
    }

    public async Task<PDFExtractionResult> ExtractPDFAsync(string blobName, byte[] pdfBytes, CancellationToken ct = default)
    {
        var fileSizeBytes = pdfBytes.LongLength;

        // Step 1: local, free structural check — rejects oversized/corrupt/encrypted/
        // too-many-page files before spending a paid Document Intelligence call on them.
        if (!PdfDocumentValidator.IsPDFValid(pdfBytes, blobName, _logger, out var pdf, out var validationError))
            return new PDFExtractionResult(false, blobName, fileSizeBytes, null, null, null, null, null, null, validationError);

        // Captured before Step 2 disposes pdf - PdfPig's own PDF spec version (e.g. 1.7),
        // otherwise only ever logged and then lost.
        var pdfSpecVersion = (double?)pdf.Version;

        // Step 2: ParseNativeMetadata takes ownership of pdf's lifetime (disposes it internally)
        // and reads everything PdfPig can offer beyond DI: native Title/Author/
        // CreationDate plus the outline/bookmark tree.
        var nativeMetadata = PdfNativeMetadataExtractor.ExtractPdfNativeMetadata(pdf, blobName, _logger);

        // Step 3: submit to Document Intelligence's prebuilt-layout model and assemble
        // pages/structural data — lives in PDFStructureExtractor.
        var structureResult = await _structureExtractor.ExtractPdfStructureAsync(pdfBytes, blobName, nativeMetadata, ct);
        if (!structureResult.Ok)
            return new PDFExtractionResult(false, blobName, fileSizeBytes, pdfSpecVersion, nativeMetadata, null, null, null, null, structureResult.Error);

        return new PDFExtractionResult(
            Ok:               true,
            BlobName:         blobName,
            FileSizeBytes:    fileSizeBytes,
            PdfSpecVersion:   pdfSpecVersion,
            NativeMetadata:   nativeMetadata,
            RawContent:       structureResult.RawContent,
            Pages:            structureResult.Pages,
            Structure:        structureResult.Structure,
            EstimatedCostUsd: structureResult.EstimatedCostUsd,
            Error:            null);
    }
}
