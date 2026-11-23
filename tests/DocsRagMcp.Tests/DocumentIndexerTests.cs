namespace DocsRagMcp.Tests;

public sealed class DocumentIndexerTests : IDisposable
{
    private readonly TestServer _server = new();

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task Indexes_supported_files_and_skips_everything_else()
    {
        var guide = _server.Docs.Write("guide.md", "# Setup\nInstall the app.");
        var todo = _server.Docs.Write("notes/todo.txt", "Buy milk.");
        _server.Docs.Write("Program.cs", "class Program {}");
        _server.Docs.Write("node_modules/package/readme.md", "Not my docs.");
        _server.Docs.Write(".git/description.md", "Not my docs either.");

        var report = await _server.Indexer.IndexFolderAsync(_server.Docs.Path);

        Assert.Equal(2, report.Added);
        Assert.Empty(report.Failed);
        Assert.Equal([guide, todo], await _server.Store.ListSourcePathsAsync());
    }

    [Fact]
    public async Task Unchanged_files_are_not_embedded_again()
    {
        _server.Docs.Write("guide.md", "# Setup\nInstall the app.");
        await _server.Indexer.IndexFolderAsync(_server.Docs.Path);
        var callsAfterFirstRun = _server.Embeddings.Calls;

        var report = await _server.Indexer.IndexFolderAsync(_server.Docs.Path);

        Assert.Equal(1, report.Unchanged);
        Assert.Equal(0, report.Added + report.Updated);
        Assert.Equal(callsAfterFirstRun, _server.Embeddings.Calls);
    }

    [Fact]
    public async Task Changed_and_deleted_files_are_picked_up()
    {
        var guide = _server.Docs.Write("guide.md", "# Setup\nInstall the app.");
        var old = _server.Docs.Write("old.md", "Outdated page.");
        await _server.Indexer.IndexFolderAsync(_server.Docs.Path);

        File.WriteAllText(guide, "# Setup\nInstall the app with winget.");
        File.Delete(old);
        var report = await _server.Indexer.IndexFolderAsync(_server.Docs.Path);

        Assert.Equal(1, report.Updated);
        Assert.Equal(1, report.Removed);
        Assert.Equal([guide], await _server.Store.ListSourcePathsAsync());
    }

    [Fact]
    public async Task Unreadable_file_is_reported_and_the_rest_is_still_indexed()
    {
        _server.Docs.Write("broken.pdf", "this is not a real PDF");
        _server.Docs.Write("guide.md", "# Setup\nInstall the app.");

        var report = await _server.Indexer.IndexFolderAsync(_server.Docs.Path);

        Assert.Equal(1, report.Added);
        Assert.Contains("broken.pdf", Assert.Single(report.Failed));
    }

    [Fact]
    public async Task Embedded_text_starts_with_the_file_name_and_section()
    {
        _server.Docs.Write("guide.md", "# Setup\nInstall the app.");

        await _server.Indexer.IndexFolderAsync(_server.Docs.Path);

        Assert.StartsWith("guide.md > Setup\n\n", Assert.Single(_server.Embeddings.Inputs));
    }
}
