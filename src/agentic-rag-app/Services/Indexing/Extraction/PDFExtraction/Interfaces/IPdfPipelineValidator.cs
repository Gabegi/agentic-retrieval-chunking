using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public interface IPdfPipelineValidator
{
    PdfValidationReport Validate(
        ExtractionResult<PdfPageRecord>          pagesExtraction,
        PdfCleanResult                            cleanResult,
        int?                                      previousRunCleanedCount = null,
        IReadOnlyList<PdfExtractionDiagnostics>?  diagnostics = null);
}
