using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

public interface IPdfCleaner
{
    PdfCleanResult CleanPdf(IReadOnlyList<PdfPageRecord> pages);
}
