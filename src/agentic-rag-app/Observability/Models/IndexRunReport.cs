namespace ProtocolsIndexer.Observability.Reports;

public record IndexRunReport(
    string         InstanceId,
    DateTimeOffset StartedAt,
    string         Source,
    bool           ForceReindex,
    int            DocsExtracted,
    int            ChunksProduced,
    int            DocsUploaded,
    bool           Success,
    string?        ErrorMessage);
