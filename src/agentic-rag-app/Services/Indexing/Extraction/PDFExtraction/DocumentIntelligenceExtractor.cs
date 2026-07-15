using System.Diagnostics.CodeAnalysis;
using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// Azure Document Intelligence ("prebuilt-layout") PDF extraction backend, ported
// from the comparison spike's DocumentIntelligenceExtractionService, with chunking
// stripped out entirely (chunking stays downstream, in ChunkingService, unchanged).
// Thin orchestrator: preflight here, everything that reads AnalyzeResult/bookmark
// structure (the paid call, markdown page assembly) lives in PDFStructureExtractor.
// Produces one PdfPageRecord per PDF page with markdown-flavored content
// ("## " headings, real column-aware pipe tables), same shape CSV's
// PageRecord.PageContent arrives in.
public class DocumentIntelligenceExtractor : IPdfExtractor
{
    public string Name => "DocumentIntelligence";

    // prebuilt-layout is billed at $10 / 1,000 pages. Verify against current
    // Azure pricing before trusting cost estimates derived from this constant.
    private const decimal CostPerPage = 0.01m;

    private readonly ILogger<DocumentIntelligenceExtractor> _logger;
    private readonly PDFStructureExtractor                   _structureExtractor;

    public DocumentIntelligenceExtractor(DocumentIntelligenceClient client, ILogger<DocumentIntelligenceExtractor>? logger = null)
    {
        _logger             = logger ?? NullLogger<DocumentIntelligenceExtractor>.Instance;
        _structureExtractor = new PDFStructureExtractor(client, _logger);
    }


    public PdfFileExtraction ExtractPDF(string blobName, byte[] pdfBytes)
    {
        // Step 1: local, free structural check — rejects oversized/corrupt/encrypted/
        // too-many-page files before spending a paid Document Intelligence call on them.
        // Metadata and bookmarks are read here too, inside the same `using` that disposes
        // the PdfDocument, since nothing later in the pipeline keeps it open.
        if (!PdfPreFlight.IsPDFValid(pdfBytes, blobName, _logger, out var pdf, out var checkError))
            return new PdfFileExtraction([], null, checkError);

        DocMetadata meta;
        IReadOnlyList<Bookmark>? bookmarks;
        using (pdf)
        {
            meta      = PdfMetadataExtraction.ParseNativeMetadata(pdf);
            bookmarks = _structureExtractor.GetBookmarks(pdf, blobName);
        }

        // Native PDF metadata is a secondary signal alongside PdfMetadataExtraction's
        // blob-name/content-derived Title/Version — not yet wired into the pipeline's
        // output (see PdfPreFlight/DocMetadata), just surfaced here for now.
        _logger.LogDebug(
            "PdfPreFlight: '{Blob}' — {Pages} page(s), title={Title}, author={Author}, created={Created}",
            blobName, meta.PageCount, meta.Title, meta.Author, meta.CreatedAt);

        // Step 2: submit to Document Intelligence's prebuilt-layout model (structure
        // extraction — the paid call and its retry/error handling — lives in
        // PDFStructureExtractor; this pipeline is synchronous end-to-end, so the async
        // call is awaited inline rather than threading async through IPdfExtractor).
        var outcome = _structureExtractor.AnalyzePDFStructureAsync(pdfBytes, blobName).GetAwaiter().GetResult();
        if (!outcome.Ok)
            return new PdfFileExtraction([], null, outcome.Error);

        var analysis = outcome.Result!;
        var pageCount = analysis.Pages?.Count ?? 0;

        // Preserve newlines so multiline regexes in PdfMetadataExtraction anchor correctly.
        var firstPagesText = string.Join("\n",
            analysis.Paragraphs?
                .Where(p => (p.BoundingRegions is { Count: > 0 } br0 ? br0[0].PageNumber : 0) <= 2)
                .Select(p => p.Content) ?? []);

        var index = PdfMetadataExtraction.Parse(blobName, firstPagesText);

        var pages = _structureExtractor.BuildMarkdownPages(blobName, analysis, pageCount, bookmarks);

        return new PdfFileExtraction(pages, index, Error: null, EstimatedCostUsd: pageCount * CostPerPage);
    }
}
