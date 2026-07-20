using CsvIndexing.Models;

namespace CsvIndexing.Services;

public interface IPipelineValidator
{
    ValidationReport Validate(
        ExtractionResult<PageRecord>  pagesExtraction,
        ExtractionResult<IndexRecord> indexExtraction,
        JoinResult                    joinResult,
        CleanResult                   cleanResult,
        int?                          previousRunCleanedCount = null);
}
