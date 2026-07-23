namespace AgenticRagApp.Common.Models;

// Output of the low-level splitter, identical in both pipelines.
public sealed record TextChunk(int Index, string Content, string? Heading = null);
