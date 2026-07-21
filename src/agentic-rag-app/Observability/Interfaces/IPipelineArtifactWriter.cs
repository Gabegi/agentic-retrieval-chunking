namespace AgenticRag.Observability.Reports;

// Persistent, full-content archive of what each pipeline step (extraction/chunking/
// embedding) actually produced - separate from IRunReportWriter's dev-only diagnostic
// reports (different container, different lifecycle: this one always writes, in every
// environment, so it's a real audit trail rather than a debug aid).
public interface IPipelineArtifactWriter
{
    // Callers build the blob path themselves (e.g. "{instanceId}/extraction.json") and
    // supply whatever serializable artifact they need written.
    Task WriteArtifactAsync<T>(string path, T artifact, CancellationToken ct = default);
}
