using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public interface ICsvExtractor
{
    ExtractionResult<PageRecord>  ExtractPages(Stream stream);
    ExtractionResult<IndexRecord> ExtractIndex(Stream stream);
}
