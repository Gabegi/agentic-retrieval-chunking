using AgenticRagApp.Indexing.Pdf.Models;

namespace AgenticRagApp.Indexing.Pdf.Services;

public interface IPdfCleaner
{
    PdfCleanResult CleanPdf(IReadOnlyList<PdfPageRecord> pages);
}
