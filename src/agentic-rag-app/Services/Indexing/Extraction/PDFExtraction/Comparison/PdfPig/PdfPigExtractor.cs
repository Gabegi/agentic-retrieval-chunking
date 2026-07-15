using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProtocolsIndexer.Models;
using UglyToad.PdfPig.Content;

namespace ProtocolsIndexer.Services;

// Orchestrates the PdfPig backend's per-file pipeline: open/validate -> baseline ->
// decoration detection -> per-page content extraction. Each step lives in its own
// class alongside this one (see PdfDocumentValidator.TryOpenAndValidate,
// PdfDocumentBaselineCalculator, PdfDecorationDetector, PdfPageContentExtractor) -
// this class just wires them together and owns the parts that are genuinely about
// one file's overall outcome (the page loop, warnings/errors, diagnostics, the final
// PdfFileExtraction).
public class PdfPigExtractor : IPdfExtractor
{
    // to compare with DI
    public string Name => "PdfPig";
    // to skip pictures only pages
    private const int MinExpectedCharsPerPage = 20;
    // DecorationTextBlockClassifier works with more than 2 docs
    private const int MinPagesForDecorationDetection = 3;

    private readonly ILogger<PdfPigExtractor> _logger;

    private readonly PdfDocumentBaselineCalculator _baselineCalculator;
    private readonly PdfDecorationDetector        _decorationDetector;

    public PdfPigExtractor(ILogger<PdfPigExtractor>? logger = null, IEnumerable<string>? knownSections = null)
    {
        _logger = logger ?? NullLogger<PdfPigExtractor>.Instance;

        _baselineCalculator  = new PdfDocumentBaselineCalculator(_logger, knownSections);
        _decorationDetector  = new PdfDecorationDetector(_logger);
    }

    // No real async work in this backend (PdfPig is fully synchronous) - no async keyword
    // to avoid a CS1998 "lacks await" warning, just wrapping the existing result so this
    // satisfies IPdfExtractor's signature, which DocumentIntelligenceExtractor needs async
    // for (real network I/O in its analyze call).
    public Task<PdfFileExtraction> ExtractPDFAsync(string blobName, byte[] pdfBytes, CancellationToken ct = default) =>
        Task.FromResult(ExtractPDF(blobName, pdfBytes));

    private PdfFileExtraction ExtractPDF(string blobName, byte[] pdfBytes)
    {
        var errors   = new List<ExtractionError>();
        var warnings = new List<ExtractionWarning>();

        if (!PdfDocumentValidator.TryOpenAndValidate(pdfBytes, blobName, _logger, out var pdf, out var openError))
            return new PdfFileExtraction([], null, openError);

        using (pdf)
        {
            var allPages = pdf.GetPages().ToList();
            var baseline = _baselineCalculator.GetDocumentBaseline(pdf, allPages, blobName);

            // One segmenter, built once and reused 
            // segmenter = finds block text by scoping white space
            var segmenter = PdfSegmenterFactory.CreateSegmenter(baseline.DominantPageWidth);

            // Checking decoration (i.e. headers, footers..)
            // Works only for docs with > 3 pages
            var decorationDetectionRan = pdf.NumberOfPages >= MinPagesForDecorationDetection;
            var decorationByPage = decorationDetectionRan
                ? _decorationDetector.GetDecorationTextByPage(allPages, segmenter, blobName)
                : new Dictionary<int, HashSet<string>>();

            var pages              = new List<PdfPageRecord>();
            string? currentHeading = null;

            for (var pageNumber = 1; pageNumber <= pdf.NumberOfPages; pageNumber++)
            {
                try
                {
                    var page = pdf.GetPage(pageNumber);
                    decorationByPage.TryGetValue(pageNumber, out var decoration);

                    var content = PdfPageContentExtractor.ExtractPageContent(
                        page, segmenter, baseline.DominantFontSize, decoration ?? [], baseline.KnownSections,
                        ref currentHeading, out var pageHadHeading);

                    // Carry the most recent heading into a page whose own text
                    // doesn't start with one — chunking is per-page downstream,
                    // so section identity can't be recovered later.
                    if (!pageHadHeading && currentHeading != null && content.Length > 0)
                        content = $"## {currentHeading}\n\n{content}";

                    if (content.Length < MinExpectedCharsPerPage)
                        warnings.Add(new ExtractionWarning
                        {
                            DocumentId = blobName,
                            RowNumber  = pageNumber,
                            Message    = $"Page {pageNumber}: only {content.Length} char(s) extracted — likely image-only (scanned) page.",
                        });

                    pages.Add(new PdfPageRecord
                    {
                        BlobName    = blobName,
                        PageIndex   = pageNumber,
                        PageContent = content,
                    });
                }
                catch (Exception ex)
                {
                    errors.Add(new ExtractionError
                    {
                        DocumentId = blobName,
                        RowNumber  = pageNumber, // reused as "page number"
                        Message    = $"Unreadable PDF page {pageNumber}: {ex.Message}",
                    });
                }
            }

            if (pages.Count == 0)
                return Failed(blobName,
                    $"All {pdf.NumberOfPages} page(s) failed extraction. First error: {errors.FirstOrDefault()?.Message}",
                    PdfOpenFailureReason.NoReadablePages);

            var index = PdfMetadataExtractor.Parse(blobName);

            var diagnostics = new PdfExtractionDiagnostics
            {
                BlobName                   = blobName,
                DominantFontSize           = baseline.DominantFontSize,
                DominantPageWidth          = baseline.DominantPageWidth,
                KnownSectionCount          = baseline.KnownSections.Count,
                BookmarksContributed       = baseline.BookmarksContributed,
                DecorationDetectionRan     = decorationDetectionRan,
                PagesWithDecorationRemoved = decorationByPage.Values.Count(d => d.Count > 0),
                ParsedTitle                = index.Title,
                ParsedVersion              = index.Version,
                ParsedPublicationDateRaw   = index.PublicationDateRaw,
                PageCount                  = pages.Count,
                PageErrorCount             = errors.Count,
                WarningCount               = warnings.Count,
            };

            return new PdfFileExtraction(pages, index, Error: null)
            {
                PageErrors  = errors,
                Warnings    = warnings,
                Diagnostics = diagnostics,
            };
        }
    }

    private static PdfFileExtraction Failed(string blobName, string message, PdfOpenFailureReason reason = PdfOpenFailureReason.Unknown) =>
        new([], null, new ExtractionError { DocumentId = blobName, Message = message, Reason = reason });
}
