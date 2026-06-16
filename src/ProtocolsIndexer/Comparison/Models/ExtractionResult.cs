namespace ProtocolsIndexer.Comparison.Models;

public class ExtractionResult
{
    public string              BlobName         { get; set; } = "";
    public string              Method           { get; set; } = "";
    public List<DocumentChunk> Chunks           { get; set; } = [];
    public long                ElapsedMs        { get; set; }
    public decimal             EstimatedCostUsd { get; set; }
    public string?             Error            { get; set; }

    public int    ChunkCount       => Chunks.Count;
    public int    EmptyChunks      => Chunks.Count(c => string.IsNullOrWhiteSpace(c.Content));
    public double AvgChunkTokens   => Chunks.Count > 0 ? Chunks.Average(c => c.TokenEstimate) : 0;
    public int    SectionsDetected => Chunks.Count(c => c.Heading != null);
    public int    ShortChunks      => Chunks.Count(c => c.TokenEstimate < 20);
}
