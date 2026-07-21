namespace AgenticRagApp.Models;

public record TextChunk(
    int     Index,
    string  Content,
    string? Heading = null
);
