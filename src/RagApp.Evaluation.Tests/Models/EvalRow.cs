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

    // Performance
    long            LatencyMs,
    int             InputTokens,
    int             OutputTokens,

    // Scores
    double          Groundedness,       // 1-5  LLM — response grounded in retrieved context?
    double          Relevance,          // 1-5  LLM — response relevant to the question?
    double          Coherence,          // 1-5  LLM — response coherent and well-formed?
    double          Equivalence,        // 1-5  LLM — same meaning as expected answer?
    double          Retrieval,          // 1-5  LLM — was the right context fetched?
    double          F1,                 // 0-1  NLP — token overlap vs expected answer

    DateTimeOffset  Timestamp);