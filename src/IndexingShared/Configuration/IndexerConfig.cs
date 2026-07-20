namespace IndexingShared.Configuration;

public class IndexerConfig
{
    public string SearchEndpoint { get; init; } = default!;
    public string OpenAiEndpoint { get; init; } = default!;
    public string OpenAiEmbeddingDeployment { get; init; } = default!;
    public string StorageAccountUrl { get; init; } = default!;
    public string StorageContainer { get; init; } = default!;
    public string SearchIndexName { get; init; } = default!;
    public string KnowledgeSourceName { get; init; } = default!;
    public string KnowledgeBaseName { get; init; } = default!;
    public string OpenAiGptDeployment { get; init; } = default!;
    public string OpenAiGptModelName { get; init; } = default!;
    public string OpenAiExtractionDeployment   { get; init; } = "gpt-41-extraction";
    public string DocumentIntelligenceEndpoint { get; init; } = "";
    public string OpenAiEmbeddingModelName     { get; init; } = "text-embedding-3-large";
    public int    OpenAiEmbeddingDimensions    { get; init; } = 3072;
}
