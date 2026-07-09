using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public interface IDataCleaner
{
    CleanResult Clean(IReadOnlyList<JoinedPageRecord> pages);
}
