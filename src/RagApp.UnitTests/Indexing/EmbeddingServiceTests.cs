using Azure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using AgenticRag.Configuration;
using AgenticRag.Models;
using AgenticRag.Services;

namespace RagApp.UnitTests.Indexing;

[TestClass]
public class EmbeddingServiceTests
{
    private static IndexerConfig Config(int dims = 4) => new()
    {
        SearchEndpoint            = "https://search.example.com",
        OpenAiEndpoint            = "https://openai.example.com",
        OpenAiEmbeddingDeployment = "embed",
        StorageAccountUrl         = "https://storage.example.com",
        StorageContainer          = "container",
        SearchIndexName           = "index",
        KnowledgeSourceName       = "ks",
        KnowledgeBaseName         = "kb",
        OpenAiGptDeployment       = "gpt",
        OpenAiGptModelName        = "gpt-model",
        OpenAiEmbeddingDimensions = dims,
    };

    private static DocumentChunk Document(string id, string content) => new()
    {
        Id      = id,
        Content = content,
    };

    private static GeneratedEmbeddings<Embedding<float>> Embeddings(int count, int dims = 4) =>
        new(Enumerable.Range(0, count).Select(_ => new Embedding<float>(new float[dims])));

    private static Mock<IEmbeddingGenerator<string, Embedding<float>>> MockGenerator() =>
        new();

    // Always-miss by default, matching the pre-cache behavior every existing test below
    // already expects (every doc actually goes through the generator). Cache-specific
    // tests build their own mock with a real hit configured.
    private static Mock<IVectorCache> MockVectorCache()
    {
        var mock = new Mock<IVectorCache>();
        mock.Setup(c => c.TryGetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((float[]?)null);
        mock.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return mock;
    }

    private static EmbeddingService BuildService(
        Mock<IEmbeddingGenerator<string, Embedding<float>>> generator,
        IndexerConfig?    config      = null,
        Mock<IVectorCache>? vectorCache = null) =>
        new(generator.Object, (vectorCache ?? MockVectorCache()).Object, config ?? Config(), NullLogger<EmbeddingService>.Instance);

    [TestMethod]
    public async Task EmbedDocumentsAsync_AllDocumentsGetContentVectorSet()
    {
        var generator = MockGenerator();
        generator
            .Setup(g => g.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> values, EmbeddingGenerationOptions? _, CancellationToken _) => Embeddings(values.Count()));
        var service = BuildService(generator);
        var docs = new[] { Document("d1", "content one"), Document("d2", "content two") };

        var result = await service.EmbedDocumentsAsync(docs);

        Assert.IsTrue(result.Documents.All(d => d.ContentVector != null));
        Assert.AreEqual(0, result.VectorDimErrors);
        Assert.AreEqual(0, result.ChunksTruncated);
        Assert.AreEqual(0, result.EmbeddingRetries);
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_WrongVectorDimensions_CountedAsDimError()
    {
        var generator = MockGenerator();
        generator
            .Setup(g => g.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> values, EmbeddingGenerationOptions? _, CancellationToken _) => Embeddings(values.Count(), dims: 3));
        var service = BuildService(generator, Config(dims: 4)); // expects 4, generator returns 3
        var docs = new[] { Document("d1", "content") };

        var result = await service.EmbedDocumentsAsync(docs);

        Assert.AreEqual(1, result.VectorDimErrors);
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_OversizedChunk_IsTruncatedBeforeEmbedding()
    {
        var oversized = new string('a', 25_000);
        string[]? capturedTexts = null;
        var generator = MockGenerator();
        generator
            .Setup(g => g.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> values, EmbeddingGenerationOptions? _, CancellationToken _) =>
            {
                capturedTexts = values.ToArray();
                return Embeddings(capturedTexts.Length);
            });
        var service = BuildService(generator);
        var docs = new[] { Document("d1", oversized) };

        var result = await service.EmbedDocumentsAsync(docs);

        Assert.AreEqual(1, result.ChunksTruncated);
        Assert.IsNotNull(capturedTexts);
        Assert.AreEqual(24_000, capturedTexts![0].Length);
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_SmallChunk_IsNotTruncated()
    {
        var generator = MockGenerator();
        generator
            .Setup(g => g.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> values, EmbeddingGenerationOptions? _, CancellationToken _) => Embeddings(values.Count()));
        var service = BuildService(generator);
        var docs = new[] { Document("d1", "short content") };

        var result = await service.EmbedDocumentsAsync(docs);

        Assert.AreEqual(0, result.ChunksTruncated);
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_EmbeddingTextIsContent()
    {
        // Title/Breadcrumb are already prepended into Content by ChunkingService before
        // EmbeddingService ever sees a chunk - EmbeddingText is just Content directly now
        // (no separate Summary fold-in - that field no longer exists).
        string[]? capturedTexts = null;
        var generator = MockGenerator();
        generator
            .Setup(g => g.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> values, EmbeddingGenerationOptions? _, CancellationToken _) =>
            {
                capturedTexts = values.ToArray();
                return Embeddings(capturedTexts.Length);
            });
        var service = BuildService(generator);
        var docs = new[] { Document("d1", "My Title\n\nbody") };

        await service.EmbedDocumentsAsync(docs);

        Assert.AreEqual("My Title\n\nbody", capturedTexts![0]);
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_MoreThanOneBatch_SplitsIntoMultipleGenerateCalls()
    {
        var callSizes = new List<int>();
        var generator = MockGenerator();
        generator
            .Setup(g => g.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> values, EmbeddingGenerationOptions? _, CancellationToken _) =>
            {
                var list = values.ToList();
                lock (callSizes) callSizes.Add(list.Count);
                return Embeddings(list.Count);
            });
        var service = BuildService(generator);
        var docs = Enumerable.Range(0, 150).Select(i => Document($"d{i}", $"content {i}")).ToArray();

        var result = await service.EmbedDocumentsAsync(docs);

        Assert.AreEqual(150, result.Documents.Count());
        Assert.AreEqual(2, callSizes.Count);
        CollectionAssert.AreEquivalent(new[] { 100, 50 }, callSizes);
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_TransientFailureThenSuccess_RetriesAndSucceeds()
    {
        var attempts = 0;
        var generator = MockGenerator();
        generator
            .Setup(g => g.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions>(), It.IsAny<CancellationToken>()))
            .Returns(async (IEnumerable<string> values, EmbeddingGenerationOptions? _, CancellationToken ct) =>
            {
                attempts++;
                if (attempts == 1)
                    throw new RequestFailedException(429, "throttled");
                return Embeddings(values.Count());
            });
        var service = BuildService(generator);
        var docs = new[] { Document("d1", "content") };

        var result = await service.EmbedDocumentsAsync(docs);

        Assert.AreEqual(2, attempts);
        Assert.AreEqual(1, result.EmbeddingRetries);
        Assert.IsTrue(result.Documents.All(d => d.ContentVector != null));
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_NonRetryableFailure_PropagatesImmediately()
    {
        var attempts = 0;
        var generator = MockGenerator();
        generator
            .Setup(g => g.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions>(), It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<string>, EmbeddingGenerationOptions?, CancellationToken>((_, _, _) =>
            {
                attempts++;
                throw new InvalidOperationException("not retryable");
            });
        var service = BuildService(generator);
        var docs = new[] { Document("d1", "content") };

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => service.EmbedDocumentsAsync(docs));
        Assert.AreEqual(1, attempts);
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_CacheHit_SkipsEmbeddingAndReusesVector()
    {
        var cachedVector = new float[] { 1, 2, 3, 4 };
        var vectorCache  = new Mock<IVectorCache>();
        vectorCache.Setup(c => c.TryGetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(cachedVector);
        var generator = MockGenerator();
        var service   = BuildService(generator, vectorCache: vectorCache);
        var docs      = new[] { Document("d1", "content one") };

        var result = await service.EmbedDocumentsAsync(docs);

        Assert.AreEqual(1, result.CacheHits);
        CollectionAssert.AreEqual(cachedVector, result.Documents.Single().ContentVector);
        generator.Verify(
            g => g.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_CacheMiss_EmbedsAndWritesResultToCache()
    {
        var vectorCache = MockVectorCache();
        var generator   = MockGenerator();
        generator
            .Setup(g => g.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> values, EmbeddingGenerationOptions? _, CancellationToken _) => Embeddings(values.Count()));
        var service = BuildService(generator, vectorCache: vectorCache);
        var docs    = new[] { Document("d1", "content one") };

        var result = await service.EmbedDocumentsAsync(docs);

        Assert.AreEqual(0, result.CacheHits);
        vectorCache.Verify(c => c.SetAsync(It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_CachedVectorWrongDimensions_TreatedAsMissAndReEmbedded()
    {
        // Cached under an older embedding config (2 dims); current config expects 4 -
        // must not be trusted blindly, has to fall back to a real embedding call.
        var vectorCache = new Mock<IVectorCache>();
        vectorCache.Setup(c => c.TryGetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new float[] { 1, 2 });
        vectorCache.Setup(c => c.SetAsync(It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var generator = MockGenerator();
        generator
            .Setup(g => g.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> values, EmbeddingGenerationOptions? _, CancellationToken _) => Embeddings(values.Count(), dims: 4));
        var service = BuildService(generator, Config(dims: 4), vectorCache);
        var docs    = new[] { Document("d1", "content one") };

        var result = await service.EmbedDocumentsAsync(docs);

        Assert.AreEqual(0, result.CacheHits);
        generator.Verify(
            g => g.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task EmbedDocumentsAsync_NoDocuments_ReturnsEmptyResultWithoutCallingGenerator()
    {
        var generator = MockGenerator();
        var service   = BuildService(generator);

        var result = await service.EmbedDocumentsAsync([]);

        Assert.AreEqual(0, result.Documents.Count());
        generator.Verify(
            g => g.GenerateAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<EmbeddingGenerationOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
