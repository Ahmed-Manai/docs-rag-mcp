using DocsRagMcp.Storage;

namespace DocsRagMcp.Tests;

public sealed class ChunkStoreTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly ChunkStore _store;

    public ChunkStoreTests() => _store = new ChunkStore(_folder.Combine("index.db"));

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task Search_returns_the_closest_chunks_first()
    {
        await _store.ReplaceSourceAsync(Doc("a.md"), "h1", [Chunk(0, "cats", 1, 0, 0), Chunk(1, "dogs", 0, 1, 0)]);
        await _store.ReplaceSourceAsync(Doc("b.md"), "h2", [Chunk(0, "lions", 0.9f, 0.1f, 0)]);

        var hits = await _store.SearchAsync(new float[] { 1, 0, 0 }, topK: 2);

        Assert.Equal(["cats", "lions"], hits.Select(hit => hit.Text));
        Assert.Equal(1f, hits[0].Score, precision: 4);
    }

    [Fact]
    public async Task Search_can_be_limited_to_a_folder()
    {
        await _store.ReplaceSourceAsync(Doc("team-a/notes.md"), "h1", [Chunk(0, "alpha", 1, 0, 0)]);
        await _store.ReplaceSourceAsync(Doc("team-b/notes.md"), "h2", [Chunk(0, "beta", 1, 0, 0)]);

        var hits = await _store.SearchAsync(new float[] { 1, 0, 0 }, topK: 5, within: [Doc("team-b")]);

        Assert.Equal("beta", Assert.Single(hits).Text);
    }

    [Fact]
    public async Task Search_can_be_limited_to_several_folders_and_files()
    {
        await _store.ReplaceSourceAsync(Doc("team-a/notes.md"), "h1", [Chunk(0, "alpha", 1, 0, 0)]);
        await _store.ReplaceSourceAsync(Doc("team-b/notes.md"), "h2", [Chunk(0, "beta", 1, 0, 0)]);
        await _store.ReplaceSourceAsync(Doc("team-c/notes.md"), "h3", [Chunk(0, "gamma", 1, 0, 0)]);
        await _store.ReplaceSourceAsync(Doc("team-c/other.md"), "h4", [Chunk(0, "delta", 1, 0, 0)]);

        var hits = await _store.SearchAsync(new float[] { 1, 0, 0 }, topK: 5, within: [Doc("team-a"), Doc("team-c/notes.md")]);

        Assert.Equal(["alpha", "gamma"], hits.Select(hit => hit.Text).Order());
    }

    [Fact]
    public async Task Folder_filter_does_not_match_a_sibling_with_the_same_prefix()
    {
        await _store.ReplaceSourceAsync(Doc("api/a.md"), "h1", [Chunk(0, "inside", 1, 0, 0)]);
        await _store.ReplaceSourceAsync(Doc("api-old/a.md"), "h2", [Chunk(0, "sibling", 1, 0, 0)]);

        var hits = await _store.SearchAsync(new float[] { 1, 0, 0 }, topK: 5, within: [Doc("api")]);

        Assert.Equal("inside", Assert.Single(hits).Text);
    }

    [Fact]
    public async Task Replacing_a_file_drops_its_old_chunks()
    {
        await _store.ReplaceSourceAsync(Doc("a.md"), "v1", [Chunk(0, "old", 1, 0, 0), Chunk(1, "old", 0, 1, 0)]);
        await _store.ReplaceSourceAsync(Doc("a.md"), "v2", [Chunk(0, "new", 1, 0, 0)]);

        var source = Assert.Single(await _store.ListSourcesAsync());
        Assert.Equal(1, source.ChunkCount);
        Assert.Equal("v2", await _store.GetContentHashAsync(Doc("a.md")));
    }

    [Fact]
    public async Task Removing_a_folder_removes_every_file_inside_it()
    {
        await _store.ReplaceSourceAsync(Doc("old/a.md"), "h1", [Chunk(0, "a", 1, 0, 0)]);
        await _store.ReplaceSourceAsync(Doc("old/sub/b.md"), "h2", [Chunk(0, "b", 1, 0, 0)]);
        await _store.ReplaceSourceAsync(Doc("keep.md"), "h3", [Chunk(0, "c", 1, 0, 0)]);

        var removed = await _store.RemoveAsync(Doc("old"));

        Assert.Equal(2, removed.Count);
        Assert.Equal([Doc("keep.md")], await _store.ListSourcePathsAsync());
        Assert.Equal("c", Assert.Single(await _store.SearchAsync(new float[] { 1, 0, 0 }, topK: 5)).Text);
    }

    [Fact]
    public async Task Index_built_with_another_embedding_model_is_rejected()
    {
        await _store.CheckEmbeddingModelAsync("ollama/model-a", claimIfUnset: true);
        await _store.ReplaceSourceAsync(Doc("a.md"), "h1", [Chunk(0, "a", 1, 0, 0)]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _store.CheckEmbeddingModelAsync("openai/model-b", claimIfUnset: false));
        Assert.Contains("ollama/model-a", error.Message);
    }

    [Fact]
    public async Task Embedding_model_is_known_once_it_is_claimed()
    {
        Assert.Null(await _store.GetEmbeddingModelAsync());

        await _store.CheckEmbeddingModelAsync("ollama/model-a", claimIfUnset: true);

        Assert.Equal("ollama/model-a", await _store.GetEmbeddingModelAsync());
    }

    [Fact]
    public async Task Empty_index_can_switch_embedding_model()
    {
        await _store.CheckEmbeddingModelAsync("ollama/model-a", claimIfUnset: true);

        await _store.CheckEmbeddingModelAsync("openai/model-b", claimIfUnset: true);
        await _store.CheckEmbeddingModelAsync("openai/model-b", claimIfUnset: false);
    }

    private string Doc(string relativePath) => _folder.Combine(relativePath.Split('/'));

    private static ChunkToStore Chunk(int ordinal, string text, params float[] vector) => new(ordinal, null, text, vector);
}
