namespace RagApp.Evaluation.Tests.Models;

public record EvalRow(
    // Identity
    string          ScenarioName,
    string          Department,        // Afdeling — for filtering/reporting
    string          Query,              // Vraag
    string          Difficulty,         // Lastigheid

    // Golden truth (what we expected)
    string          ExpectedAnswer,     // Antwoord
    string          ExpectedSources,    // Bronnen

    // Actual output
    string          Response,
    string          RetrievedContext,
    bool            Succeeded,
    string          Error,

    // Performance
    long            LatencyMs,
    int             InputTokens,
    int             OutputTokens,
    double          CostUsd,            // (InputTokens × inputPrice + OutputTokens × outputPrice) / 1M

    // Scores
    double          Groundedness,       // 1-5  LLM — response grounded in retrieved context?
    double          Relevance,          // 1-5  LLM — response relevant to the question?
    double          Coherence,          // 1-5  LLM — response coherent and well-formed?
    double          Equivalence,        // 1-5  LLM — same meaning as expected answer?
    // double       Retrieval,          // 1-5  LLM — was the right context fetched?  (re-enable with Retrieval)
    // double       F1,                 // 0-1  NLP — token overlap vs expected answer (re-enable with F1)

     DateTimeOffset Timestamp)
{
    /// <summary>Builds a row representing a failed RAG call, with all scores zeroed.</summary>
    public static EvalRow ForFailure(TestQuery q, string error, long latencyMs) => new(
        ScenarioName: q.Name,
        Department: q.Department,
        Query: q.Query,
        Difficulty: q.Difficulty,
        ExpectedAnswer: q.ExpectedAnswer,
        ExpectedSources: q.ExpectedSources,
        Response: "",
        RetrievedContext: "",
        Succeeded: false,
        Error: error,
        LatencyMs: latencyMs,
        InputTokens: 0,
        OutputTokens: 0,
        CostUsd: 0,
        Groundedness: 0, Relevance: 0, Coherence: 0,
        Equivalence: 0,
        // Retrieval: 0,  // re-enable with Retrieval
        // F1: 0,         // re-enable with F1
        Timestamp: DateTimeOffset.UtcNow);
}