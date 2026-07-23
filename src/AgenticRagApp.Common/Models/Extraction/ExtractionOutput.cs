namespace AgenticRagApp.Common.Models;

public sealed record ExtractionOutput(IReadOnlyList<ExtractionDocument> Docs) : ExtractionOutputBase;
