using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public interface ICsvJoiner
{
    JoinResult Join(IReadOnlyList<PageRecord> pages, IReadOnlyList<IndexRecord> index);
}
