namespace KnowledgeBaseManager.Configuration;

public class KnowledgeBaseConfig
{
    public string SearchEndpoint      { get; init; } = default!;
    public string OpenAiEndpoint      { get; init; } = default!;
    public string OpenAiGptDeployment { get; init; } = default!;
    public string OpenAiGptModelName  { get; init; } = default!;
    public string SearchIndexName     { get; init; } = default!;
    public string KnowledgeSourceName { get; init; } = default!;
    public string KnowledgeBaseName   { get; init; } = default!;
}
