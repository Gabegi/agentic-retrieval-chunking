using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ProtocolsIndexer.Observability;

internal static class Instrumentation
{
    internal const string ActivitySourceName = "ProtocolsIndexer";
    internal const string MeterName          = "ProtocolsIndexer";

    internal static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");
    internal static readonly Meter          Meter          = new(MeterName, "1.0.0");

    internal static readonly Counter<long>   BlobsProcessed  = Meter.CreateCounter<long>("indexer.blobs_processed",  description: "Blobs fully processed through the indexing pipeline");
    internal static readonly Counter<long>   UploadFailures  = Meter.CreateCounter<long>("indexer.upload_failures",  description: "Individual document upload failures");
    internal static readonly Histogram<long> ChunksExtracted = Meter.CreateHistogram<long>("indexer.chunks_extracted", unit: "chunks", description: "Chunks produced per blob");
}
