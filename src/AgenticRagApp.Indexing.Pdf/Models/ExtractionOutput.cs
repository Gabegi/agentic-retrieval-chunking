using AgenticRagApp.Common.Models;

namespace AgenticRagApp.Indexing.Pdf.Models;

public sealed record ExtractionOutput(IReadOnlyList<ExtractionDocument> Docs) : ExtractionOutputBase;
