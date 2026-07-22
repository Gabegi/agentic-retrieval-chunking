using Azure;
using Azure.AI.DocumentIntelligence;
using Moq;
using AgenticRagApp.Infrastructure.Clients.DocumentIntelligence;

namespace RagApp.UnitTests.Infrastructure.DocumentIntelligence;

[TestClass]
public class DocumentAnalysisClientTests
{
    [TestMethod]
    public async Task SubmitAnalyzeAsync_DelegatesToUnderlyingClient_AndReturnsItsOperation()
    {
        var operation = new Mock<Operation<AnalyzeResult>>().Object;
        var underlying = new Mock<DocumentIntelligenceClient>();
        var options    = new AnalyzeDocumentOptions("prebuilt-layout", BinaryData.FromBytes([1, 2, 3]));
        underlying
            .Setup(c => c.AnalyzeDocumentAsync(WaitUntil.Started, options, It.IsAny<CancellationToken>()))
            .ReturnsAsync(operation);
        var client = new DocumentAnalysisClient(underlying.Object);

        var result = await client.SubmitAnalyzeAsync(options);

        Assert.AreSame(operation, result);
        underlying.Verify(c => c.AnalyzeDocumentAsync(WaitUntil.Started, options, It.IsAny<CancellationToken>()), Times.Once);
    }
}
