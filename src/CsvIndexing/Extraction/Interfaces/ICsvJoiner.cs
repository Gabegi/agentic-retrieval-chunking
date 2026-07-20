using CsvIndexing.Models;

namespace CsvIndexing.Services;

public interface ICsvJoiner
{
    JoinResult Join(IReadOnlyList<PageRecord> pages, IReadOnlyList<IndexRecord> index);
}
