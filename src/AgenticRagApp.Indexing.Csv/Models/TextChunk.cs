namespace AgenticRagApp.Indexing.Csv.Models;

public record TextChunk(
    int     Index,
    string  Content,
    string? Heading = null
);
