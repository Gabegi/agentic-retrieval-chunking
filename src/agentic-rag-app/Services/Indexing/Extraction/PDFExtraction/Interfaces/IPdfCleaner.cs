using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public interface IPdfCleaner
{
    PdfCleanResult Clean(IReadOnlyList<PdfJoinedPageRecord> pages);
}
