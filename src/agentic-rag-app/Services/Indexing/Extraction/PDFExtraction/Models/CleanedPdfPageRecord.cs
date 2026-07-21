namespace AgenticRag.Models;

public class CleanedPdfPageRecord
{
    public string BlobName    { get; set; } = "";
    public int    PageNumber  { get; set; }
    public string PageContent { get; set; } = "";
    public string Title       { get; set; } = "";
}
