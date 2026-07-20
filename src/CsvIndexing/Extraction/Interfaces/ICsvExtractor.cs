using CsvIndexing.Models;

namespace CsvIndexing.Services;

public interface ICsvExtractor
{
    ExtractionResult<PageRecord>  ExtractPages(Stream stream);
    ExtractionResult<IndexRecord> ExtractIndex(Stream stream);
}
