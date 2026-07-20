using CsvIndexing.Models;

namespace CsvIndexing.Services;

public interface IDataCleaner
{
    CleanResult Clean(IReadOnlyList<JoinedPageRecord> pages);
}
