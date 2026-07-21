using AgenticRag.Models;

namespace AgenticRag.Services;

public interface IPdfCleaner
{
    PdfCleanResult CleanPdf(IReadOnlyList<PdfPageRecord> pages);
}
