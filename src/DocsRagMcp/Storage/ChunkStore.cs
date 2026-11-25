using System.Globalization;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace DocsRagMcp.Storage;

public sealed record ChunkToStore(int Ordinal, string? Location, string Text, ReadOnlyMemory<float> Embedding);

public sealed record SourceInfo(string Path, int ChunkCount, DateTimeOffset IndexedAt);

public sealed record SearchHit(string Path, string? Location, string Text, float Score);

/// <summary>
/// Stores document chunks and their embeddings in a single SQLite file. Search compares the query
/// vector with every chunk by cosine similarity (a linear scan is fast enough for tens of thousands
/// of chunks), and can also rank chunks by keywords with SQLite's built-in FTS5 full-text index.
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

        -- Folders indexed with index_folder, which are watched and kept in sync.
        CREATE TABLE IF NOT EXISTS folders (
            path       TEXT PRIMARY KEY,
            indexed_at TEXT NOT NULL
        );

        -- Full-text index over the chunks table, kept in sync by the triggers below. The porter
        -- tokenizer matches word forms, so "deploy" also finds "deploying" and "deployment".
        CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(
            location, text, content = 'chunks', content_rowid = 'id', tokenize = 'porter unicode61');
        CREATE TRIGGER IF NOT EXISTS chunks_fts_insert AFTER INSERT ON chunks BEGIN
            INSERT INTO chunks_fts (rowid, location, text) VALUES (new.id, new.location, new.text);
        END;
        CREATE TRIGGER IF NOT EXISTS chunks_fts_delete AFTER DELETE ON chunks BEGIN
            INSERT INTO chunks_fts (chunks_fts, rowid, location, text) VALUES ('delete', old.id, old.location, old.text);
        END;
        CREATE TRIGGER IF NOT EXISTS chunks_fts_update AFTER UPDATE ON chunks BEGIN
            INSERT INTO chunks_fts (chunks_fts, rowid, location, text) VALUES ('delete', old.id, old.location, old.text);
            INSERT INTO chunks_fts (rowid, location, text) VALUES (new.id, new.location, new.text);
        END;
        """;

    private const string EmbeddingModelKey = "embedding_model";
    private const string KeywordIndexKey = "keyword_index";
    private const string KeywordIndexVersion = "1";

    // Share of the vector score in a hybrid score; the keyword score gets the rest. Keywords settle
    // close calls between passages with similar meaning, but never outvote a clearly better
    // meaning match. Chosen with samples/eval: lower values helped technical docs a little more but
    // started to push the right answer down on plain-language questions.
    private const double VectorWeight = 0.9;

    // How many of the best results from each ranking take part in the fusion.
    private const int MinCandidates = 50;

    // A match in a heading (the chunk's location) counts twice as much as one in the body text.
    private const string Bm25 = "bm25(chunks_fts, 2.0, 1.0)";

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
        using (var command = connection.CreateCommand())
        {
            command.CommandText = Schema;
            command.ExecuteNonQuery();
        }

        BuildKeywordIndexIfMissing(connection);
    }

    /// <summary>The embedding model the index was built with, or null if nothing was indexed yet.</summary>
    public async Task<string?> GetEmbeddingModelAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return (string?)await ScalarAsync(
            connection, "SELECT value FROM metadata WHERE key = $key", cancellationToken, ("$key", EmbeddingModelKey));
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
            ("$indexedAt", Now()));

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

    public Task<IReadOnlyList<string>> ListSourcePathsAsync(CancellationToken cancellationToken = default) =>
        ListPathsAsync("SELECT path FROM sources ORDER BY path", cancellationToken);

    /// <summary>Remembers a folder that was indexed, so it can be watched and kept in sync.</summary>
    public async Task AddFolderAsync(string folder, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO folders (path, indexed_at) VALUES ($path, $indexedAt)
            ON CONFLICT (path) DO UPDATE SET indexed_at = excluded.indexed_at
            """,
            cancellationToken,
            ("$path", folder),
            ("$indexedAt", Now()));
    }

    /// <summary>The folders that were indexed, sorted by path so a folder comes before its subfolders.</summary>
    public Task<IReadOnlyList<string>> ListFoldersAsync(CancellationToken cancellationToken = default) =>
        ListPathsAsync("SELECT path FROM folders ORDER BY path", cancellationToken);

    /// <summary>Forgets a folder and every indexed folder inside it. Returns the folders that were forgotten.</summary>
    public async Task<IReadOnlyList<string>> RemoveFoldersAsync(string pathOrFolder, CancellationToken cancellationToken = default)
    {
        var removed = (await ListFoldersAsync(cancellationToken))
            .Where(folder => PathUtils.IsSameOrUnder(folder, pathOrFolder))
            .ToList();

        await using var connection = await OpenAsync(cancellationToken);
        foreach (var folder in removed)
        {
            await ExecuteAsync(connection, "DELETE FROM folders WHERE path = $path", cancellationToken, ("$path", folder));
        }

        return removed;
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

    /// <summary>
    /// Returns the <paramref name="topK"/> most relevant chunks, best first. Without
    /// <paramref name="keywords"/>, chunks are ranked by cosine similarity to the query vector.
    /// With them, each candidate also gets a keyword score (BM25 over the full-text index), both
    /// scores are scaled to 0..1 and blended, so a passage that uses the exact terms of the question
    /// rises when its meaning is about as close as the others'. The returned score is always the
    /// cosine similarity, so it reads the same in both modes.
    /// </summary>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        ReadOnlyMemory<float> query,
        int topK,
        string? withinFolder = null,
        string? keywords = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(topK, 1);

        await using var connection = await OpenAsync(cancellationToken);

        var similarities = await ScoreAllAsync(connection, query, withinFolder, cancellationToken);
        if (similarities.Count == 0)
        {
            return [];
        }

        var candidates = Math.Max(MinCandidates, topK * 5);
        var byVector = similarities
            .OrderByDescending(pair => pair.Value)
            .Take(candidates)
            .Select(pair => pair.Key)
            .ToList();

        var keywordQuery = keywords is null ? null : KeywordQuery.Build(keywords);
        var keywordScores = keywordQuery is null
            ? []
            : await ScoreByKeywordsAsync(connection, keywordQuery, similarities, candidates, cancellationToken);

        var ranked = keywordScores.Count == 0
            ? byVector.Take(topK).ToList()
            : Blend(byVector, keywordScores, similarities).Take(topK).ToList();

        var hits = await LoadHitsAsync(connection, ranked, similarities, cancellationToken);
        return ranked.Select(id => hits[id]).ToList();
    }

    /// <summary>
    /// Ranks the candidates from both lists by a weighted sum of their vector and keyword scores.
    /// Cosine similarities are scaled so the candidates span 0..1, and BM25 scores are divided by the
    /// best one; a candidate the keyword search didn't find gets a keyword score of 0.
    /// </summary>
    private static IEnumerable<long> Blend(
        List<long> byVector, Dictionary<long, double> keywordScores, Dictionary<long, float> similarities)
    {
        var candidates = byVector.Union(keywordScores.Keys).ToList();
        var lowest = candidates.Min(id => similarities[id]);
        var range = candidates.Max(id => similarities[id]) - lowest;
        var bestKeyword = keywordScores.Values.Max();

        return candidates
            .Select(id =>
            {
                var vector = range > 0 ? (similarities[id] - lowest) / range : 1.0;
                var keyword = bestKeyword > 0 ? keywordScores.GetValueOrDefault(id) / bestKeyword : 0.0;
                return (Id: id, Score: (VectorWeight * vector) + ((1 - VectorWeight) * keyword));
            })
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => similarities[candidate.Id])
            .Select(candidate => candidate.Id);
    }

    /// <summary>Computes the cosine similarity of every chunk (inside the folder, if given) to the query.</summary>
    private static async Task<Dictionary<long, float>> ScoreAllAsync(
        SqliteConnection connection, ReadOnlyMemory<float> query, string? withinFolder, CancellationToken cancellationToken)
    {
        var similarities = new Dictionary<long, float>();

        await using var command = connection.CreateCommand();
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
            if (!float.IsNaN(score))
            {
                similarities[reader.GetInt64(0)] = score;
            }
        }

        return similarities;
    }

    /// <summary>
    /// Scores the best keyword matches with BM25 (higher is better). Only chunks that took part in
    /// the vector scan (same folder, same embedding size) are kept, so both scores cover the same chunks.
    /// </summary>
    private static async Task<Dictionary<long, double>> ScoreByKeywordsAsync(
        SqliteConnection connection,
        string keywordQuery,
        Dictionary<long, float> eligible,
        int limit,
        CancellationToken cancellationToken)
    {
        var scores = new Dictionary<long, double>(limit);

        // SQLite's bm25() returns lower values for better matches, so it is negated.
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT rowid, -{Bm25} FROM chunks_fts WHERE chunks_fts MATCH $query ORDER BY {Bm25}";
        command.Parameters.AddWithValue("$query", keywordQuery);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (scores.Count < limit && await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(0);
            if (eligible.ContainsKey(id))
            {
                scores[id] = reader.GetDouble(1);
            }
        }

        return scores;
    }

    private static async Task<Dictionary<long, SearchHit>> LoadHitsAsync(
        SqliteConnection connection, List<long> ids, Dictionary<long, float> similarities, CancellationToken cancellationToken)
    {
        var hits = new Dictionary<long, SearchHit>(ids.Count);

        await using var command = connection.CreateCommand();
        var names = ids.Select((_, i) => $"$id{i}").ToList();
        command.CommandText = $"SELECT id, source_path, location, text FROM chunks WHERE id IN ({string.Join(", ", names)})";
        for (var i = 0; i < ids.Count; i++)
        {
            command.Parameters.AddWithValue(names[i], ids[i]);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt64(0);
            hits[id] = new SearchHit(
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3),
                similarities[id]);
        }

        return hits;
    }

    /// <summary>
    /// Indexes built before keyword search existed have chunks but an empty full-text index.
    /// Fill it once from the chunks table, so upgrading doesn't require re-indexing.
    /// </summary>
    private static void BuildKeywordIndexIfMissing(SqliteConnection connection)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT value FROM metadata WHERE key = $key";
        check.Parameters.AddWithValue("$key", KeywordIndexKey);
        if (check.ExecuteScalar() as string == KeywordIndexVersion)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        using (var rebuild = connection.CreateCommand())
        {
            rebuild.CommandText = """
                INSERT INTO chunks_fts (chunks_fts) VALUES ('rebuild');
                INSERT INTO metadata (key, value) VALUES ($key, $value)
                ON CONFLICT (key) DO UPDATE SET value = excluded.value;
                """;
            rebuild.Parameters.AddWithValue("$key", KeywordIndexKey);
            rebuild.Parameters.AddWithValue("$value", KeywordIndexVersion);
            rebuild.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private async Task<IReadOnlyList<string>> ListPathsAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        var paths = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            paths.Add(reader.GetString(0));
        }

        return paths;
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
