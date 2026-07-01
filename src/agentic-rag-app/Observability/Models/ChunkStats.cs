namespace ProtocolsIndexer.Observability.Reports;

public record ChunkStats(
    int    ChunksProduced,
    int    DocsWithZeroChunks,
    int    DuplicateChunks,
    long   MinChunkSizeChars,
    long   MaxChunkSizeChars,
    double AvgChunkSizeChars,
    long   P95ChunkSizeChars,
    int    BandUnder100,
    int    Band100To500,
    int    Band500To1500,
    int    Band1500Plus
);
