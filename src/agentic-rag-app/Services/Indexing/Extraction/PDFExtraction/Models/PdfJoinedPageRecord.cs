namespace ProtocolsIndexer.Models;

// Mirrors CSV's JoinedPageRecord — one page's content merged with its document's
// parsed metadata.
public class PdfJoinedPageRecord
{
    // from PdfPageRecord
    public string BlobName    { get; set; } = "";
    public int    PageIndex   { get; set; }
    public string PageContent { get; set; } = "";

    // from PdfIndexRecord
    public string Title              { get; set; } = "";
    public string Version            { get; set; } = "";
    public string PublicationDateRaw { get; set; } = "";
}
