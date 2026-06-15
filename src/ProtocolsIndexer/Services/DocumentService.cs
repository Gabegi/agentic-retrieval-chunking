using System.Collections.Concurrent;

namespace ProtocolsIndexer.Services;

public class DocumentService : IDocumentService
{
    private readonly BlobContainerClient _containerClient;
    private readonly SearchClient _searchClient;
    private readonly ILogger<DocumentService> _logger;

    public DocumentService(
        IndexerConfig config,
        BlobServiceClient blobServiceClient,
        TokenCredential credential,
        ILogger<DocumentService> logger)
    {
        _containerClient = blobServiceClient.GetBlobContainerClient(config.StorageContainer);
        _searchClient    = new SearchClient(new Uri(config.SearchEndpoint), config.SearchIndexName, credential);
        _logger          = logger;
    }

    public async Task<IEnumerable<BlobItem>> ReadBlobsAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Reading blobs from container {Container}", _containerClient.Name);

        var blobs = new List<BlobItem>();

        await foreach (var blob in _containerClient.GetBlobsAsync(cancellationToken: ct))
        {
            if (!blob.Name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Skipping non-PDF blob {Name}", blob.Name);
                continue;
            }
            blobs.Add(blob);
        }

        var existingIds = await GetExistingIdsAsync(blobs, ct);
        var newBlobs    = blobs.Where(b => !existingIds.Contains(BlobNameToId(b.Name))).Take(500).ToList();

        _logger.LogInformation("Found {Total} PDF blobs — {New} new (capped at 500), {Skipped} already indexed",
            blobs.Count, newBlobs.Count, blobs.Count - newBlobs.Count);

        return newBlobs;
    }

    public async Task<IEnumerable<ProtocolDocument>> ExtractDocumentsAsync(
        IEnumerable<BlobItem> blobs,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Extracting text from {Count} PDFs", blobs.Count());

        var documents = new ConcurrentBag<ProtocolDocument>();

        await Parallel.ForEachAsync(blobs,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (blob, token) =>
            {
                try
                {
                    var doc = await ExtractSingleDocumentAsync(blob, token);
                    if (doc != null)
                        documents.Add(doc);
                }
                catch (Exception ex)
                {
                    _logger.LogError("Failed to process {Name}: {Error}", blob.Name, ex.Message);
                }
            });

        var docList      = documents.ToList();
        var emptyContent = docList.Count(d => string.IsNullOrWhiteSpace(d.Content));

        _logger.LogInformation("Extracted {Count} documents ({Empty} with empty content)",
            docList.Count, emptyContent);

        return docList;
    }

    private async Task<ProtocolDocument?> ExtractSingleDocumentAsync(BlobItem blob, CancellationToken ct)
    {
        var sw      = System.Diagnostics.Stopwatch.StartNew();
        var bytes   = await DownloadBlobAsync(blob, ct);
        var content = ExtractTextFromPdf(bytes);

        if (string.IsNullOrWhiteSpace(content))
            _logger.LogWarning("Empty text from {Name} — likely scanned PDF", blob.Name);
        else if (content.Length < 100)
            _logger.LogWarning("Short text ({Chars} chars) for {Name}", content.Length, blob.Name);

        _logger.LogInformation("Extracted {Name} — {Chars} chars in {Ms}ms",
            blob.Name, content.Length, sw.ElapsedMilliseconds);

        return new ProtocolDocument
        {
            Id            = BlobNameToId(blob.Name),
            SourceFile    = blob.Name,
            RichtlijnName = blob.Name.Split('/')[0],
            Content       = content
        };
    }

    private async Task<byte[]> DownloadBlobAsync(BlobItem blob, CancellationToken ct)
    {
        var blobClient = _containerClient.GetBlobClient(blob.Name);
        await using var stream = await blobClient.OpenReadAsync(cancellationToken: ct);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private string ExtractTextFromPdf(byte[] pdfBytes)
    {
        using var pdf = PdfDocument.Open(pdfBytes);
        var pages     = pdf.GetPages().ToList();
        var text      = string.Join("\n", pages.Select(p => string.Join(" ", p.GetWords().Select(w => w.Text))));

        _logger.LogInformation("PdfPig: {Chars} chars from {Pages} pages", text.Length, pages.Count);
        return text;
    }

    private static string BlobNameToId(string blobName) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(blobName))
            .Replace("+", "-").Replace("/", "_").Replace("=", "");

    private async Task<HashSet<string>> GetExistingIdsAsync(IEnumerable<BlobItem> blobs, CancellationToken ct)
    {
        var ids      = blobs.Select(b => BlobNameToId(b.Name)).ToList();
        var existing = new HashSet<string>();

        foreach (var batch in ids.Chunk(50))
        {
            var filter  = string.Join(" or ", batch.Select(id => $"id eq '{id}'"));
            var options = new Azure.Search.Documents.SearchOptions { Filter = filter, Size = 1000 };
            options.Select.Add("id");

            var results = await _searchClient.SearchAsync<Azure.Search.Documents.Models.SearchDocument>(
                "*", options, ct);

            await foreach (var result in results.Value.GetResultsAsync())
                existing.Add(result.Document["id"]?.ToString() ?? "");
        }

        return existing;
    }
}
