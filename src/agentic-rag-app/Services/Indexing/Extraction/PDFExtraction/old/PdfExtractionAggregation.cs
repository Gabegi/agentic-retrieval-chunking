using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// Folds one-file-at-a-time PdfFileExtraction results (from IPdfExtractor.Extract,
// called once per PDF blob) into the same batch-level ExtractionResult<T> shape
// CsvExtractor already hands to ICsvJoiner in one go. A file-level extraction error
// (corrupt PDF, backend exception) is recorded once against pages - a file that
// failed to parse contributes nothing.
public static class PdfExtractionAggregation
{
    public static ExtractionResult<PdfPageRecord> Aggregate(IEnumerable<PdfFileExtraction> fileResults)
    {
        var pages = new ExtractionResult<PdfPageRecord>();

        foreach (var file in fileResults)
        {
            if (file.Error != null)
            {
                pages.AddError(file.Error);
                continue;
            }

            foreach (var page in file.Pages) pages.AddRecord(page);

            foreach (var pageError in file.PageErrors) pages.AddError(pageError);
            foreach (var warning in file.Warnings) pages.AddWarning(warning);
        }

        return pages;
    }
}
