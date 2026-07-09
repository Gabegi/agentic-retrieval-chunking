using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public interface IPdfPipelineValidator
{
    PdfValidationReport Validate(
        ExtractionResult<PdfPageRecord>  pagesExtraction,
        ExtractionResult<PdfIndexRecord> indexExtraction,
        PdfJoinResult                    joinResult,
        PdfCleanResult                   cleanResult,
        int?                             previousRunCleanedCount = null);
}
