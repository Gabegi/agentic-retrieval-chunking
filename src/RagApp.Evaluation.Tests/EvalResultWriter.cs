using System.Text;
using System.Text.Json;
using Azure.Storage.Blobs;
using RagApp.Evaluation.Tests.Models;

namespace RagApp.Evaluation.Tests.Evaluation;

/// <summary>
/// Appends EvalRows as JSONL to blob storage. Knows nothing about how scores
/// were computed — just persists whatever EvalRow it's given.
/// </summary>
public sealed class EvalResultWriter
{
    private readonly BlobContainerClient _container;
    private readonly string _executionId;

    public EvalResultWriter(BlobContainerClient container, string? executionId = null)
    {
        _container = container;
        _executionId = executionId ?? DateTime.UtcNow.ToString("yyyyMMddTHHmmss");
    }

    public async Task WriteAsync(EvalRow row, CancellationToken ct = default)
    {
        // One blob per run (date + executionId) — avoids concurrent-append collisions
        // if MSTest runs test methods in parallel.
        var blobName = $"eval-results/{DateTime.UtcNow:yyyy-MM-dd}/{_executionId}.jsonl";
        var blob = _container.GetAppendBlobClient(blobName);
        await blob.CreateIfNotExistsAsync(cancellationToken: ct);

        var line = JsonSerializer.Serialize(row) + "\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(line));
        await blob.AppendBlockAsync(stream, cancellationToken: ct);
    }
}