using AgenticRagApp.Indexing.Csv.Models;

namespace AgenticRagApp.Indexing.Csv.Services;

public interface ICsvExtractor
{
    ExtractionResult<PageRecord>  ExtractPages(Stream stream);
    ExtractionResult<IndexRecord> ExtractIndex(Stream stream);
}
