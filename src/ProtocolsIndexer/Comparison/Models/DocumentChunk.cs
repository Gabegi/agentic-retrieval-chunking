namespace ProtocolsIndexer.Comparison.Models;

public class DocumentChunk
{
    public string  Content    { get; set; } = "";
    public string? Heading    { get; set; }
    public int     PageNumber { get; set; }
    public int     TokenEstimate => Content.Split(' ').Length;
}
