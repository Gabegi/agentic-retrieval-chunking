using System.ClientModel.Primitives;
using Azure.Search.Documents.KnowledgeBases.Models;
using AgenticRag.Services;

namespace RagApp.UnitTests.Querying;

[TestClass]
public class KnowledgeBaseActivitySummaryTests
{
    // These are Azure SDK response-only models (no public constructor) - built via
    // ModelReaderWriter from JSON, the SDK's documented pattern for constructing them in tests.
    private static KnowledgeBaseModelQueryPlanningActivityRecord PlanningRecord(long? input, long? output) =>
        ModelReaderWriter.Read<KnowledgeBaseModelQueryPlanningActivityRecord>(BinaryData.FromString(
            $$"""{"type":"modelQueryPlanning","inputTokens":{{input?.ToString() ?? "null"}},"outputTokens":{{output?.ToString() ?? "null"}}}"""))!;

    private static KnowledgeBaseModelAnswerSynthesisActivityRecord SynthesisRecord(long? input, long? output) =>
        ModelReaderWriter.Read<KnowledgeBaseModelAnswerSynthesisActivityRecord>(BinaryData.FromString(
            $$"""{"type":"modelAnswerSynthesis","inputTokens":{{input?.ToString() ?? "null"}},"outputTokens":{{output?.ToString() ?? "null"}}}"""))!;

    [TestMethod]
    public void NullActivity_ReturnsZeroTokens()
    {
        var (input, output) = KnowledgeBaseActivitySummary.SumTokens(null);

        Assert.AreEqual(0, input);
        Assert.AreEqual(0, output);
    }

    [TestMethod]
    public void EmptyActivity_ReturnsZeroTokens()
    {
        var (input, output) = KnowledgeBaseActivitySummary.SumTokens([]);

        Assert.AreEqual(0, input);
        Assert.AreEqual(0, output);
    }

    [TestMethod]
    public void PlanningRecord_TokensAreSummed()
    {
        var record = PlanningRecord(10, 20);

        var (input, output) = KnowledgeBaseActivitySummary.SumTokens([record]);

        Assert.AreEqual(10, input);
        Assert.AreEqual(20, output);
    }

    [TestMethod]
    public void SynthesisRecord_TokensAreSummed()
    {
        var record = SynthesisRecord(5, 7);

        var (input, output) = KnowledgeBaseActivitySummary.SumTokens([record]);

        Assert.AreEqual(5, input);
        Assert.AreEqual(7, output);
    }

    [TestMethod]
    public void MultipleRecords_TokensAcrossPlanningAndSynthesisAreSummed()
    {
        var planning  = PlanningRecord(10, 20);
        var synthesis = SynthesisRecord(5, 7);

        var (input, output) = KnowledgeBaseActivitySummary.SumTokens([planning, synthesis]);

        Assert.AreEqual(15, input);
        Assert.AreEqual(27, output);
    }

    [TestMethod]
    public void NullTokenValues_AreTreatedAsZero()
    {
        var record = PlanningRecord(null, null);

        var (input, output) = KnowledgeBaseActivitySummary.SumTokens([record]);

        Assert.AreEqual(0, input);
        Assert.AreEqual(0, output);
    }
}
