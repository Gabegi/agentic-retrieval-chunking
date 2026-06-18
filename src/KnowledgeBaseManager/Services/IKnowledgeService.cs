namespace KnowledgeBaseManager.Services;

public interface IKnowledgeService
{
    Task EnsureKnowledgeSourceAsync(CancellationToken ct = default);
    Task EnsureKnowledgeBaseAsync(CancellationToken ct = default);
}
