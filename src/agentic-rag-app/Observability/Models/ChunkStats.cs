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
    int    Band1500Plus,
    // Token-based quality signals (1 token ≈ 1 word)
    int    OversizedChunks,   // token estimate > 1024 — may exceed LLM context budgets
    int    UndersizedChunks,  // token estimate < 20 — too short to carry useful meaning
    double AvgTokenEstimate,
    // Structural quality
    int    CoherentChunks,    // starts with uppercase/digit AND ends with punctuation
    int    HeadingsDetected   // chunks with a heading field set
);
