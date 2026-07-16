namespace ProtocolsIndexer.Models;

// One page's raw extracted content from a single PDF file — mirrors CSV's PageRecord.
// PageContent is already markdown-flavored ("## " headings, pipe-row tables) by the
// time it leaves the extractor, same shape CSV's PageRecord.PageContent arrives in.
// Title is the whole document's title (same value on every page of one file) - set once
// when the pages are built, not joined in from a separate index record.
public class PdfPageRecord
{
    public string BlobName    { get; set; } = "";
    public int    PageIndex   { get; set; }
    public string PageContent { get; set; } = "";
    public string Title       { get; set; } = "";
}
