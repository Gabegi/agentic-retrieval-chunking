namespace AgenticRagApp.Common.Models;

// The three fields every extracted document has, regardless of source.
public abstract record ExtractionDocumentBase(string SourceId, int Ordinal, string Content);
