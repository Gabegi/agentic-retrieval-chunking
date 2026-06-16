namespace ProtocolsIndexer.Models;

public class ExtractionRun
{
    public string                 ServiceName      { get; set; } = "";
    public string                 BlobName         { get; set; } = "";
    public List<ProtocolDocument> Chunks           { get; set; } = [];
    public long                   ElapsedMs        { get; set; }
    public decimal                EstimatedCostUsd { get; set; }
    public bool                   UsedFallback     { get; set; }
    public string?                Error            { get; set; }

    public int    ChunkCount       => Chunks.Count;
    public int    EmptyChunks      => Chunks.Count(c => c.IsEmpty);
    public int    OversizedChunks  => Chunks.Count(c => c.IsOversized);
    public int    UndersizedChunks => Chunks.Count(c => c.IsUndersized);
    public double AvgTokens        => Chunks.Count > 0 ? Chunks.Average(c => c.TokenEstimate) : 0;
    public int    HeadingsDetected => Chunks.Count(c => c.Heading != null);
}
