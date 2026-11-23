using System.Text.RegularExpressions;
using DocsRagMcp.Indexing;
using DocsRagMcp.Ingestion;
using DocsRagMcp.Storage;
using DocsRagMcp.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace DocsRagMcp.Tests;

/// <summary>A temporary folder that is deleted when the test ends.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "docs-rag-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string Write(string relativePath, string content)
    {
        var fullPath = Combine(relativePath.Split('/'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    public void Dispose()
    {
        // Pooled SQLite connections keep the database file open, which blocks deleting it on Windows.
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// Deterministic stand-in for a real embedding model: each word adds weight to one dimension,
/// so texts that share words get similar vectors. Good enough to test ranking without a network.
/// </summary>
internal sealed partial class FakeEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private const int Dimensions = 256;

    public int Calls { get; private set; }

    public List<string> Inputs { get; } = [];

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        var embeddings = new List<Embedding<float>>();
        foreach (var value in values)
        {
            Inputs.Add(value);
            embeddings.Add(new Embedding<float>(Embed(value)));
        }

        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
    }

    public static float[] Embed(string text)
    {
        var vector = new float[Dimensions];
        foreach (var word in Words().Split(text.ToLowerInvariant()).Where(word => word.Length > 2))
        {
            vector[Fnv1a(word) % Dimensions] += 1;
        }

        var length = MathF.Sqrt(vector.Sum(x => x * x));
        return length == 0 ? vector : vector.Select(x => x / length).ToArray();
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private static uint Fnv1a(string text)
    {
        var hash = 2166136261;
        foreach (var c in text)
        {
            hash = (hash ^ c) * 16777619;
        }

        return hash;
    }

    [GeneratedRegex(@"\W+")]
    private static partial Regex Words();
}

/// <summary>The server's services wired together the same way as in Program.cs, with fake embeddings.</summary>
internal sealed class TestServer : IDisposable
{
    private readonly TempFolder _data = new();

    public TestServer(IEnumerable<string>? allowedPaths = null)
    {
        Options = new DocsRagOptions
        {
            Provider = EmbeddingProvider.Ollama,
            Model = "fake-model",
            DatabasePath = _data.Combine("index.db"),
        };

        Store = new ChunkStore(Options.DatabasePath);
        Indexer = new DocumentIndexer(
            Store,
            Embeddings,
            new TextChunker(400, 80),
            new PathGuard(allowedPaths ?? []),
            Options,
            NullLogger<DocumentIndexer>.Instance);
        Tools = new DocsTools(Indexer, Store, Embeddings, Options);
    }

    public TempFolder Docs { get; } = new();

    public FakeEmbeddingGenerator Embeddings { get; } = new();

    public DocsRagOptions Options { get; }

    public ChunkStore Store { get; }

    public DocumentIndexer Indexer { get; }

    public DocsTools Tools { get; }

    public void Dispose()
    {
        Docs.Dispose();
        _data.Dispose();
    }
}
