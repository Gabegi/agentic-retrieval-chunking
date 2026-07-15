using System.Diagnostics.CodeAnalysis;
using Azure;
using Azure.AI.DocumentIntelligence;
using Microsoft.Extensions.Logging;
using ProtocolsIndexer.Models;

namespace ProtocolsIndexer.Services;

// Step 2 of DocumentIntelligenceExtractor's pipeline: submits the already-preflighted
// PDF to Azure Document Intelligence's prebuilt-layout model and waits for the result.
// Isolated from ExtractPDF so the one paid, network-dependent call in the whole
// pipeline - and its exception handling - lives in one obvious place.
public sealed class DocumentIntelligenceAnalyzer
{
    private const string ModelId = "prebuilt-layout";

    private readonly DocumentIntelligenceClient _client;
    private readonly ILogger                    _logger;

    public DocumentIntelligenceAnalyzer(DocumentIntelligenceClient client, ILogger logger)
    {
        _client = client;
        _logger = logger;
    }

    public bool TryAnalyzePDF(
        byte[] pdfBytes, string blobName,
        [NotNullWhen(true)]  out AnalyzeResult?   result,
        [NotNullWhen(false)] out ExtractionError? error)
    {
        try
        {
            var operation = _client.AnalyzeDocument(
                WaitUntil.Completed, ModelId, BinaryData.FromBytes(pdfBytes));

            result = operation.Value;
            error  = null;
            return true;
        }
        catch (Exception ex)
        {
            result = null;
            error  = new ExtractionError { DocumentId = blobName, Message = ex.Message };
            _logger.LogWarning(ex, "Document Intelligence analyze call failed for '{Blob}'.", blobName);
            return false;
        }
    }
}
