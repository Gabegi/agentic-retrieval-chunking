namespace ProtocolsIndexer.Models;

// Mirrors CSV's CleanedPageRecord.
public class CleanedPdfPageRecord
{
    public string    BlobName        { get; set; } = "";
    public int       PageIndex       { get; set; }
    public string    PageContent     { get; set; } = "";
    public string    Title           { get; set; } = "";
    public string    Version         { get; set; } = "";
    public DateTime? PublicationDate { get; set; }
}
