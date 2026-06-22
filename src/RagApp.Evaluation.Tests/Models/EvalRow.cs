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
    double          Groundedness,
    double          Relevance,
    double          Coherence,
    double          Equivalence,        // NEW — Response vs ExpectedAnswer

    DateTimeOffset  Timestamp);