namespace RagApp.Evaluation.Tests.Models;

public record TestQuery(
    string Name,            // short id for the scenario, e.g. "wlz-huisdier"
    string Department,      // Afdeling
    string Query,           // Vraag
    string ExpectedAnswer,  // Antwoord
    string ExpectedSources, // Bronnen
    string Difficulty,      // Lastigheid — Low/Medium/High (or blank)
    string Value,           // Waarde — business-case notes; not used in scoring, just carried for docs
    bool   AnswerableFromCorpus = true);  // false = known corpus gap: expect abstention, skip F1