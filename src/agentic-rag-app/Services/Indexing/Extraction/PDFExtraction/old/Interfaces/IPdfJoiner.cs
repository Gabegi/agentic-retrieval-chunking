using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public interface IPdfJoiner
{
    PdfJoinResult Join(IReadOnlyList<PdfPageRecord> pages, IReadOnlyList<PdfIndexRecord> index);
}
