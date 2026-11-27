using DocsRagMcp.Storage;
using Microsoft.Data.Sqlite;

namespace DocsRagMcp.Tests;

public sealed class HybridSearchTests : IDisposable
{
    private static readonly float[] Query = [1, 0, 0];

    private readonly TempFolder _folder = new();
    private readonly ChunkStore _store;

    public HybridSearchTests() => _store = new ChunkStore(_folder.Combine("index.db"));

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task Keywords_do_not_outvote_a_clearly_better_meaning_match()
    {
        // The second chunk shares a word with the question but is far from it in meaning.
        await _store.ReplaceSourceAsync(Doc("a.md"), "h1",
        [
            Chunk(0, "You can claim up to 60 EUR per day for meals.", 1, 0, 0),
            Chunk(1, "This page explains travel expenses.", 0.3f, 0.95f, 0),
            Filler(2),
        ]);

        var hits = await _store.SearchAsync(Query, topK: 1, keywords: "Can I expense a beer with dinner?");

        Assert.StartsWith("You can claim", hits[0].Text);
    }

    [Fact]
    public async Task Keyword_match_lifts_a_passage_that_vectors_rank_lower()
    {
        // Vectors alone prefer the first chunk; only the second one contains the word asked about.
        await _store.ReplaceSourceAsync(Doc("a.md"), "h1",
        [
            Chunk(0, "Writing messages to the console", 1, 0, 0),
            Chunk(1, "Turn on authentication with a JWT bearer token", 0.9f, 0.1f, 0),
            Filler(2),
        ]);

        var vectorOnly = await _store.SearchAsync(Query, topK: 2);
        var hybrid = await _store.SearchAsync(Query, topK: 2, keywords: "How do I add JWT authentication?");

        Assert.StartsWith("Writing messages", vectorOnly[0].Text);
        Assert.StartsWith("Turn on authentication", hybrid[0].Text);
    }

    [Fact]
    public async Task Hybrid_search_reports_the_cosine_similarity_as_the_score()
    {
        await _store.ReplaceSourceAsync(Doc("a.md"), "h1", [Chunk(0, "deploy with docker", 0.6f, 0.8f, 0)]);

        var hit = Assert.Single(await _store.SearchAsync(Query, topK: 1, keywords: "docker"));

        Assert.Equal(0.6f, hit.Score, precision: 4);
    }

    [Fact]
    public async Task Headings_count_as_keywords()
    {
        await _store.ReplaceSourceAsync(Doc("a.md"), "h1",
        [
            Chunk(0, "General notes about the project.", 1, 0, 0),
            new ChunkToStore(1, "Pagination", "Use the cursor from the previous response.", new float[] { 0.8f, 0.2f, 0 }),
            Filler(2),
        ]);

        var hits = await _store.SearchAsync(Query, topK: 1, keywords: "pagination");

        Assert.Equal("Pagination", hits[0].Location);
    }

    [Fact]
    public async Task Keywords_match_other_forms_of_the_same_word()
    {
        await _store.ReplaceSourceAsync(Doc("a.md"), "h1",
        [
            Chunk(0, "Unrelated text about colours.", 1, 0, 0),
            Chunk(1, "Deploying the server to a container.", 0.7f, 0.3f, 0),
            Filler(2),
        ]);

        var hits = await _store.SearchAsync(Query, topK: 1, keywords: "how to deploy");

        Assert.StartsWith("Deploying", hits[0].Text);
    }

    [Fact]
    public async Task Replaced_text_is_no_longer_found_by_keywords()
    {
        // The new chunk reuses the old chunk's row id, so a stale keyword index would credit it with "zebra".
        await _store.ReplaceSourceAsync(Doc("a.md"), "v1", [Chunk(0, "the zebra section", 0.99f, 0.1f, 0)]);
        await _store.ReplaceSourceAsync(Doc("a.md"), "v2", [Chunk(0, "the giraffe section", 0.99f, 0.1f, 0)]);
        await _store.ReplaceSourceAsync(Doc("b.md"), "h1", [Chunk(0, "closest by vector", 1, 0, 0), Filler(1)]);

        var hits = await _store.SearchAsync(Query, topK: 1, keywords: "zebra");

        Assert.Equal("closest by vector", hits[0].Text);
    }

    [Fact]
    public async Task Removed_files_are_no_longer_found_by_keywords()
    {
        await _store.ReplaceSourceAsync(Doc("old/a.md"), "h1", [Chunk(0, "kubernetes manifests", 0, 1, 0)]);
        await _store.ReplaceSourceAsync(Doc("keep.md"), "h2", [Chunk(0, "something else", 1, 0, 0)]);

        await _store.RemoveAsync(Doc("old"));
        var hits = await _store.SearchAsync(Query, topK: 5, keywords: "kubernetes");

        Assert.Equal("something else", Assert.Single(hits).Text);
    }

    [Fact]
    public async Task Keyword_matches_respect_the_folder_filter()
    {
        await _store.ReplaceSourceAsync(Doc("team-a/notes.md"), "h1", [Chunk(0, "redis cache settings", 0, 1, 0)]);
        await _store.ReplaceSourceAsync(Doc("team-b/notes.md"), "h2", [Chunk(0, "postgres settings", 1, 0, 0)]);

        var hits = await _store.SearchAsync(Query, topK: 5, within: [Doc("team-b")], keywords: "redis");

        Assert.Equal("postgres settings", Assert.Single(hits).Text);
    }

    [Fact]
    public async Task Folder_filter_applies_before_ranking_in_both_searches()
    {
        // Far more passages outside the folder are better matches, by meaning and by keywords, than
        // the candidate limit takes. The passages inside the folder must still be found.
        for (var i = 0; i < 120; i++)
        {
            await _store.ReplaceSourceAsync(Doc($"other/{i}.md"), $"o{i}", [Chunk(0, "redis redis cache settings", 1, 0, 0)]);
        }

        await _store.ReplaceSourceAsync(Doc("team/a.md"), "t1",
        [
            Chunk(0, "redis settings for the team", 0.2f, 1, 0),
            Chunk(1, "nothing about caches", 0.6f, 0.8f, 0),
        ]);

        var hybrid = await _store.SearchAsync(Query, topK: 2, within: [Doc("team")], keywords: "redis");
        var vector = await _store.SearchAsync(Query, topK: 2, within: [Doc("team")]);

        Assert.Equal(["nothing about caches", "redis settings for the team"], hybrid.Select(hit => hit.Text));
        Assert.Equal(["nothing about caches", "redis settings for the team"], vector.Select(hit => hit.Text));
    }

    [Theory]
    [InlineData("\"; DROP TABLE chunks; --")]
    [InlineData("NEAR(a b) AND OR NOT")]
    [InlineData("col:value * ^ - + ( )")]
    [InlineData("?")]
    public async Task Any_question_text_is_safe_as_keywords(string keywords)
    {
        await _store.ReplaceSourceAsync(Doc("a.md"), "h1", [Chunk(0, "some text", 1, 0, 0)]);

        var hits = await _store.SearchAsync(Query, topK: 3, keywords: keywords);

        Assert.Equal("some text", Assert.Single(hits).Text);
    }

    [Fact]
    public async Task Index_from_an_older_version_gets_a_keyword_index_on_open()
    {
        var path = _folder.Combine("old-index.db");
        CreateVersion010Index(path, "Configure the reverse proxy headers.", "Something unrelated.");

        var store = new ChunkStore(path);
        var hits = await store.SearchAsync(Query, topK: 1, keywords: "proxy");

        Assert.Equal("Configure the reverse proxy headers.", hits[0].Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("redis cache")]
    public async Task Asking_for_more_results_does_not_reorder_the_first_ones(string? keywords)
    {
        // More chunks than the candidate pool, so the extra results come from beyond it.
        var random = new Random(42);
        var chunks = Enumerable.Range(0, 80)
            .Select(i => Chunk(
                i,
                i % 7 == 0 ? $"Passage {i} about the redis cache." : $"Passage {i} about something else.",
                (float)random.NextDouble(),
                (float)random.NextDouble(),
                (float)random.NextDouble()))
            .ToList();
        await _store.ReplaceSourceAsync(Doc("a.md"), "h1", chunks);

        var top = await _store.SearchAsync(Query, topK: 3, keywords: keywords);
        var more = await _store.SearchAsync(Query, topK: 3, keywords: keywords, limit: 70);

        Assert.Equal(top, more.Take(3));
        Assert.Equal(70, more.Count);
        Assert.Equal(70, more.Select(hit => hit.Text).Distinct().Count());
    }

    /// <summary>Writes an index the way docs-rag-mcp 0.1.0 did: same tables, no full-text index.</summary>
    private static void CreateVersion010Index(string path, string keywordText, string vectorText)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE sources (path TEXT PRIMARY KEY, content_hash TEXT NOT NULL, indexed_at TEXT NOT NULL);
            CREATE TABLE chunks (
                id INTEGER PRIMARY KEY, source_path TEXT NOT NULL, ordinal INTEGER NOT NULL,
                location TEXT, text TEXT NOT NULL, embedding BLOB NOT NULL);
            INSERT INTO sources VALUES ('/docs/a.md', 'h1', '2026-11-23T00:00:00Z');
            INSERT INTO chunks (source_path, ordinal, location, text, embedding) VALUES ('/docs/a.md', 0, NULL, $keyword, $far);
            INSERT INTO chunks (source_path, ordinal, location, text, embedding) VALUES ('/docs/a.md', 1, NULL, $vector, $near);
            INSERT INTO chunks (source_path, ordinal, location, text, embedding) VALUES ('/docs/a.md', 2, NULL, 'Gardening.', $filler);
            """;
        command.Parameters.AddWithValue("$keyword", keywordText);
        command.Parameters.AddWithValue("$vector", vectorText);
        command.Parameters.AddWithValue("$far", Bytes(0.99f, 0.1f, 0));
        command.Parameters.AddWithValue("$filler", Bytes(0, 0, 1));
        command.Parameters.AddWithValue("$near", Bytes(1, 0, 0));
        command.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private static byte[] Bytes(params float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private string Doc(string relativePath) => _folder.Combine(relativePath.Split('/'));

    private static ChunkToStore Chunk(int ordinal, string text, params float[] vector) => new(ordinal, null, text, vector);

    /// <summary>An unrelated passage, so the candidates span a realistic range of similarities.</summary>
    private static ChunkToStore Filler(int ordinal) => Chunk(ordinal, "Notes on gardening and tomatoes.", 0, 0, 1);
}
