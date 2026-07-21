using AgenticRagApp.Models;

namespace AgenticRagApp.Services;

public interface IPdfCleaner
{
    PdfCleanResult CleanPdf(IReadOnlyList<PdfPageRecord> pages);
}
