using Azure.Search.Documents.KnowledgeBases.Models;
using ProtocolsIndexer.Services;

namespace RagApp.UnitTests.Querying;

[TestClass]
public class KnowledgeBaseActivitySummaryTests
{
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
        var record = new KnowledgeBaseModelQueryPlanningActivityRecord(1)
        {
            InputTokens  = 10,
            OutputTokens = 20,
        };

        var (input, output) = KnowledgeBaseActivitySummary.SumTokens([record]);

        Assert.AreEqual(10, input);
        Assert.AreEqual(20, output);
    }

    [TestMethod]
    public void SynthesisRecord_TokensAreSummed()
    {
        var record = new KnowledgeBaseModelAnswerSynthesisActivityRecord(1)
        {
            InputTokens  = 5,
            OutputTokens = 7,
        };

        var (input, output) = KnowledgeBaseActivitySummary.SumTokens([record]);

        Assert.AreEqual(5, input);
        Assert.AreEqual(7, output);
    }

    [TestMethod]
    public void MultipleRecords_TokensAcrossPlanningAndSynthesisAreSummed()
    {
        var planning = new KnowledgeBaseModelQueryPlanningActivityRecord(1) { InputTokens = 10, OutputTokens = 20 };
        var synthesis = new KnowledgeBaseModelAnswerSynthesisActivityRecord(2) { InputTokens = 5, OutputTokens = 7 };

        var (input, output) = KnowledgeBaseActivitySummary.SumTokens([planning, synthesis]);

        Assert.AreEqual(15, input);
        Assert.AreEqual(27, output);
    }

    [TestMethod]
    public void NullTokenValues_AreTreatedAsZero()
    {
        var record = new KnowledgeBaseModelQueryPlanningActivityRecord(1);

        var (input, output) = KnowledgeBaseActivitySummary.SumTokens([record]);

        Assert.AreEqual(0, input);
        Assert.AreEqual(0, output);
    }
}
