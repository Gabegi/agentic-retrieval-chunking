using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// Folds one-file-at-a-time PdfFileExtraction results (from IPdfExtractor.Extract,
// called once per PDF blob) into the same batch-level ExtractionResult<T> shape
// CsvExtractor already hands to ICsvJoiner in one go. A file-level extraction error
// (corrupt PDF, backend exception) is recorded once against both pages and index -
// a file that failed to parse contributes to neither.
public static class PdfExtractionAggregation
{
    public static (ExtractionResult<PdfPageRecord> Pages, ExtractionResult<PdfIndexRecord> Index) Aggregate(
        IEnumerable<PdfFileExtraction> fileResults)
    {
        var pages = new ExtractionResult<PdfPageRecord>();
        var index = new ExtractionResult<PdfIndexRecord>();

        foreach (var file in fileResults)
        {
            if (file.Error != null)
            {
                pages.AddError(file.Error);
                index.AddError(file.Error);
                continue;
            }

            foreach (var page in file.Pages) pages.AddRecord(page);
            if (file.Index != null) index.AddRecord(file.Index);
        }

        return (pages, index);
    }
}
