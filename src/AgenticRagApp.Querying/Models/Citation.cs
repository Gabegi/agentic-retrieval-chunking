namespace AgenticRagApp.Querying.Models;

// ZenyaDocumentId/Version/Status/Url: PDF-only, sourced from blob metadata set at upload
// time (see ZenyaMetadata in AgenticRagApp.Indexing.Pdf) - not guaranteed to be present.
public sealed record Citation(
    string  DocumentId,
    string? Title,
    string? QuickCode,
    string? RelativePath,
    string? ZenyaDocumentId = null,
    string? ZenyaVersion    = null,
    string? ZenyaStatus     = null,
    string? ZenyaUrl        = null)
{
    // A citation with no Zenya document id can't be traced back to its source in Zenya -
    // this is the "meetgat" (measurement gap) the traceability scenario calls for, not
    // something to silently paper over. True for every PDF citation today until whoever
    // uploads a document starts setting zenya_document_id as blob metadata.
    public bool TraceabilityGap => ZenyaDocumentId is null;
}
