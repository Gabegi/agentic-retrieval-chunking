namespace AgenticRagApp.Indexing.Pdf.Models;

public record TextChunk(
    int     Index,
    string  Content,
    string? Heading = null
);
