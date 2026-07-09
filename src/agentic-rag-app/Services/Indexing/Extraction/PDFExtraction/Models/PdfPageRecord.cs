namespace ProtocolsIndexer.Models;

// One page's raw extracted content from a single PDF file — mirrors CSV's PageRecord.
// PageContent is already markdown-flavored ("## " headings, pipe-row tables) by the
// time it leaves the extractor, same shape CSV's PageRecord.PageContent arrives in.
public class PdfPageRecord
{
    public string BlobName    { get; set; } = "";
    public int    PageIndex   { get; set; }
    public string PageContent { get; set; } = "";
}
