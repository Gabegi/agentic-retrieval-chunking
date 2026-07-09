namespace ProtocolsIndexer.Models;

// One PDF file's document-level metadata, parsed from its own filename/first-page
// text (there's no external index file for PDFs) — mirrors CSV's IndexRecord.
public class PdfIndexRecord
{
    public string BlobName          { get; set; } = "";
    public string Title             { get; set; } = "";
    public string Version           { get; set; } = "";
    public string PublicationDateRaw { get; set; } = "";
}
