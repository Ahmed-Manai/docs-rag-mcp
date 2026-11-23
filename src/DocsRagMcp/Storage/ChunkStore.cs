using System.Globalization;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace DocsRagMcp.Storage;

public sealed record ChunkToStore(int Ordinal, string? Location, string Text, ReadOnlyMemory<float> Embedding);

public sealed record SourceInfo(string Path, int ChunkCount, DateTimeOffset IndexedAt);

public sealed record SearchHit(string Path, string? Location, string Text, float Score);

/// <summary>
/// Stores document chunks and their embeddings in a single SQLite file, and finds the chunks
/// closest to a query by cosine similarity. A linear scan is fast enough for tens of thousands
/// of chunks and needs no extra database extension.
/// </summary>
public sealed class ChunkStore
{
    private const string Schema = """
        PRAGMA journal_mode = WAL;
        CREATE TABLE IF NOT EXISTS metadata (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS sources (
            path         TEXT PRIMARY KEY,
            content_hash TEXT NOT NULL,
            indexed_at   TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS chunks (
            id          INTEGER PRIMARY KEY,
            source_path TEXT NOT NULL,
            ordinal     INTEGER NOT NULL,
            location    TEXT,
            text        TEXT NOT NULL,
            embedding   BLOB NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_chunks_source_path ON chunks (source_path);
        """;

    private const string EmbeddingModelKey = "embedding_model";

    private readonly string _connectionString;

    public ChunkStore(string databasePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Schema;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Makes sure the index is only ever used with one embedding model. An empty index adopts the
    /// configured model; an index that already holds vectors from another model is rejected.
    /// </summary>
    public async Task CheckEmbeddingModelAsync(string modelId, bool claimIfUnset, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);

        var existing = (string?)await ScalarAsync(
            connection, "SELECT value FROM metadata WHERE key = $key", cancellationToken, ("$key", EmbeddingModelKey));

        if (existing == modelId)
        {
            return;
        }

        if (existing is not null)
        {
            var sourceCount = Convert.ToInt64(
                await ScalarAsync(connection, "SELECT COUNT(*) FROM sources", cancellationToken), CultureInfo.InvariantCulture);

            if (sourceCount > 0)
            {
                throw new InvalidOperationException(
                    $"This index was built with the embedding model '{existing}', but the server is configured for '{modelId}'. " +
                    $"Vectors from different models can't be compared: switch back to '{existing}', or set DOCSRAG_DATABASE to a new file.");
            }
        }

        if (claimIfUnset)
        {
            await ExecuteAsync(
                connection,
                "INSERT INTO metadata (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
                cancellationToken,
                ("$key", EmbeddingModelKey),
                ("$value", modelId));
        }
    }

    public async Task<string?> GetContentHashAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return (string?)await ScalarAsync(
            connection, "SELECT content_hash FROM sources WHERE path = $path", cancellationToken, ("$path", path));
    }

    /// <summary>Replaces everything stored for a file in one transaction.</summary>
    public async Task ReplaceSourceAsync(
        string path, string contentHash, IReadOnlyList<ChunkToStore> chunks, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteAsync(connection, "DELETE FROM chunks WHERE source_path = $path", cancellationToken, ("$path", path));
        await ExecuteAsync(
            connection,
            """
            INSERT INTO sources (path, content_hash, indexed_at) VALUES ($path, $hash, $indexedAt)
            ON CONFLICT (path) DO UPDATE SET content_hash = excluded.content_hash, indexed_at = excluded.indexed_at
            """,
            cancellationToken,
            ("$path", path),
            ("$hash", contentHash),
            ("$indexedAt", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO chunks (source_path, ordinal, location, text, embedding)
            VALUES ($path, $ordinal, $location, $text, $embedding)
            """;
        insert.Parameters.AddWithValue("$path", path);
        var ordinal = insert.Parameters.Add("$ordinal", SqliteType.Integer);
        var location = insert.Parameters.Add("$location", SqliteType.Text);
        var text = insert.Parameters.Add("$text", SqliteType.Text);
        var embedding = insert.Parameters.Add("$embedding", SqliteType.Blob);

        foreach (var chunk in chunks)
        {
            ordinal.Value = chunk.Ordinal;
            location.Value = (object?)chunk.Location ?? DBNull.Value;
            text.Value = chunk.Text;
            embedding.Value = MemoryMarshal.AsBytes(chunk.Embedding.Span).ToArray();
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Removes a file, or every file inside a folder. Returns the paths that were removed.</summary>
    public async Task<IReadOnlyList<string>> RemoveAsync(string pathOrFolder, CancellationToken cancellationToken = default)
    {
        var removed = (await ListSourcePathsAsync(cancellationToken))
            .Where(path => PathUtils.IsSameOrUnder(path, pathOrFolder))
            .ToList();

        if (removed.Count == 0)
        {
            return removed;
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        foreach (var path in removed)
        {
            await ExecuteAsync(connection, "DELETE FROM chunks WHERE source_path = $path", cancellationToken, ("$path", path));
            await ExecuteAsync(connection, "DELETE FROM sources WHERE path = $path", cancellationToken, ("$path", path));
        }

        await transaction.CommitAsync(cancellationToken);
        return removed;
    }

    public async Task<IReadOnlyList<string>> ListSourcePathsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM sources ORDER BY path";

        var paths = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            paths.Add(reader.GetString(0));
        }

        return paths;
    }

    public async Task<IReadOnlyList<SourceInfo>> ListSourcesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.path, COUNT(c.id), s.indexed_at
            FROM sources s LEFT JOIN chunks c ON c.source_path = s.path
            GROUP BY s.path, s.indexed_at
            ORDER BY s.path
            """;

        var sources = new List<SourceInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sources.Add(new SourceInfo(
                reader.GetString(0),
                reader.GetInt32(1),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture)));
        }

        return sources;
    }

    /// <summary>Returns the <paramref name="topK"/> chunks most similar to the query vector, best first.</summary>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        ReadOnlyMemory<float> query, int topK, string? withinFolder = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(topK, 1);

        await using var connection = await OpenAsync(cancellationToken);

        // Min-heap of the best scores seen so far: the weakest of the top K is always on top.
        var best = new PriorityQueue<long, float>();
        var scores = new Dictionary<long, float>();

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, source_path, embedding FROM chunks";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                if (withinFolder is not null && !PathUtils.IsSameOrUnder(reader.GetString(1), withinFolder))
                {
                    continue;
                }

                var vector = MemoryMarshal.Cast<byte, float>(reader.GetFieldValue<byte[]>(2));
                if (vector.Length != query.Length)
                {
                    continue;
                }

                var score = TensorPrimitives.CosineSimilarity(query.Span, vector);
                if (float.IsNaN(score))
                {
                    continue;
                }

                var id = reader.GetInt64(0);
                if (best.Count < topK)
                {
                    best.Enqueue(id, score);
                    scores[id] = score;
                }
                else if (best.TryPeek(out var weakest, out var weakestScore) && score > weakestScore)
                {
                    best.DequeueEnqueue(id, score);
                    scores.Remove(weakest);
                    scores[id] = score;
                }
            }
        }

        if (scores.Count == 0)
        {
            return [];
        }

        var hits = new List<SearchHit>(scores.Count);
        await using (var command = connection.CreateCommand())
        {
            var ids = scores.Keys.ToList();
            var names = ids.Select((_, i) => $"$id{i}").ToList();
            command.CommandText = $"SELECT id, source_path, location, text FROM chunks WHERE id IN ({string.Join(", ", names)})";
            for (var i = 0; i < ids.Count; i++)
            {
                command.Parameters.AddWithValue(names[i], ids[i]);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                hits.Add(new SearchHit(
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3),
                    scores[reader.GetInt64(0)]));
            }
        }

        return hits.OrderByDescending(hit => hit.Score).ToList();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<object?> ScalarAsync(
        SqliteConnection connection, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = CreateCommand(connection, sql, parameters);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is DBNull ? null : result;
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = CreateCommand(connection, sql, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqliteCommand CreateCommand(SqliteConnection connection, string sql, (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }
}
