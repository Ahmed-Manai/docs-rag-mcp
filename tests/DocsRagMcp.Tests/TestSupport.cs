using System.Text.RegularExpressions;
using DocsRagMcp.Indexing;
using DocsRagMcp.Ingestion;
using DocsRagMcp.Reranking;
using DocsRagMcp.Resources;
using DocsRagMcp.Storage;
using DocsRagMcp.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;

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

    public TestServer(
        IEnumerable<string>? allowedPaths = null,
        bool watch = true,
        IRerankClient? rerankClient = null,
        int rerankCandidates = 20,
        SearchMode searchMode = SearchMode.Hybrid)
    {
        Options = new DocsRagOptions
        {
            Provider = EmbeddingProvider.Ollama,
            Model = "fake-model",
            DatabasePath = _data.Combine("index.db"),
            Watch = watch,
            SearchMode = searchMode,
            RerankEndpoint = rerankClient is null ? null : "http://localhost:8012/v1/rerank",
            RerankCandidates = rerankCandidates,
        };

        Store = new ChunkStore(Options.DatabasePath);
        var pathGuard = new PathGuard(allowedPaths ?? []);
        Indexer = new DocumentIndexer(
            Store,
            Embeddings,
            new TextChunker(400, 80),
            pathGuard,
            Options,
            NullLogger<DocumentIndexer>.Instance);
        Watcher = new FolderWatcher(Indexer, Store, Options, NullLogger<FolderWatcher>.Instance)
        {
            Delay = TimeSpan.FromMilliseconds(200),
        };
        Reranker = new Reranker(rerankClient, Options, Logger)
        {
            Timeout = TimeSpan.FromMilliseconds(500),
        };
        Tools = new DocsTools(Indexer, Watcher, Store, Embeddings, Reranker, Options);
        Resources = new DocsResources(Store, pathGuard, Indexer, new McpServerResourceCollection(), NullLogger<DocsResources>.Instance);
    }

    public TempFolder Docs { get; } = new();

    public FakeEmbeddingGenerator Embeddings { get; } = new();

    public DocsRagOptions Options { get; }

    public ChunkStore Store { get; }

    public DocumentIndexer Indexer { get; }

    public FolderWatcher Watcher { get; }

    public DocsTools Tools { get; }

    public Reranker Reranker { get; }

    public ListLogger<Reranker> Logger { get; } = new();

    public DocsResources Resources { get; }

    public void Dispose()
    {
        Watcher.Dispose();
        Docs.Dispose();
        _data.Dispose();
    }
}

/// <summary>Keeps the messages logged during a test, so a test can check what was reported.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}

/// <summary>
/// Deterministic stand-in for a cross-encoder: <see cref="Score"/> decides each document's score
/// (by default, how many words of the query it contains), and every call is recorded.
/// </summary>
internal sealed class FakeRerankClient : IRerankClient
{
    public Func<string, IReadOnlyList<string>, IReadOnlyList<double?>> Score { get; init; } = CountQueryWords;

    public TimeSpan Delay { get; init; }

    public List<(string Query, IReadOnlyList<string> Documents)> Calls { get; } = [];

    public async Task<IReadOnlyList<double?>> ScoreAsync(string query, IReadOnlyList<string> documents, CancellationToken cancellationToken)
    {
        Calls.Add((query, documents));
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        return Score(query, documents);
    }

    private static double?[] CountQueryWords(string query, IReadOnlyList<string> documents)
    {
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return documents
            .Select(document => (double?)words.Count(word => document.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }
}
