using DocsRagMcp.Indexing;

namespace DocsRagMcp.Tests;

public sealed class FolderWatcherTests : IDisposable
{
    // File system events can take a moment to arrive, especially on macOS.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly TestServer _server = new();

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task Indexed_folders_are_remembered()
    {
        _server.Docs.Write("guide.md", "# Setup\nInstall the app.");

        await _server.Indexer.IndexFolderAsync(_server.Docs.Path);

        Assert.Equal([_server.Docs.Path], await _server.Store.ListFoldersAsync());
    }

    [Fact]
    public async Task New_changed_and_deleted_files_are_indexed_automatically()
    {
        var guide = _server.Docs.Write("guide.md", "# Setup\nInstall the app.");
        var old = _server.Docs.Write("old.md", "Outdated page.");
        var result = await _server.Tools.IndexFolderAsync(_server.Docs.Path);
        var guideHash = await _server.Store.GetContentHashAsync(guide);

        var faq = _server.Docs.Write("notes/faq.md", "# Questions\nAsk in the team channel.");
        File.WriteAllText(guide, "# Setup\nInstall the app with winget.");
        File.Delete(old);

        Assert.Contains("The folder is watched", result);
        await WaitUntilAsync(async () =>
            (await _server.Store.ListSourcePathsAsync()).Order().SequenceEqual(new[] { guide, faq }.Order()) &&
            await _server.Store.GetContentHashAsync(guide) != guideHash);
    }

    [Fact]
    public async Task Changes_made_while_the_server_was_stopped_are_indexed_when_it_starts()
    {
        _server.Docs.Write("guide.md", "# Setup\nInstall the app.");
        await _server.Indexer.IndexFolderAsync(_server.Docs.Path);
        var faq = _server.Docs.Write("faq.md", "# Questions\nAsk in the team channel.");

        await _server.Watcher.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(async () => (await _server.Store.ListSourcePathsAsync()).Contains(faq));
            Assert.True(_server.Watcher.IsWatching(_server.Docs.Path));
        }
        finally
        {
            await _server.Watcher.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Removing_a_folder_from_the_index_stops_watching_it()
    {
        _server.Docs.Write("guide.md", "# Setup\nInstall the app.");
        await _server.Tools.IndexFolderAsync(_server.Docs.Path);

        var result = await _server.Tools.RemoveFromIndexAsync(_server.Docs.Path);
        _server.Docs.Write("faq.md", "# Questions\nAsk in the team channel.");
        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.Contains("Stopped watching", result);
        Assert.False(_server.Watcher.IsWatching(_server.Docs.Path));
        Assert.Empty(await _server.Store.ListSourcePathsAsync());
        Assert.Empty(await _server.Store.ListFoldersAsync());
    }

    [Fact]
    public async Task Removing_a_file_inside_a_watched_folder_says_it_can_come_back()
    {
        var guide = _server.Docs.Write("guide.md", "# Setup\nInstall the app.");
        await _server.Tools.IndexFolderAsync(_server.Docs.Path);

        var result = await _server.Tools.RemoveFromIndexAsync(guide);

        Assert.Contains($"Removed {guide}", result);
        Assert.Contains("inside a watched folder", result);
    }

    [Fact]
    public async Task A_sync_that_was_waiting_does_not_bring_back_a_removed_folder()
    {
        _server.Docs.Write("guide.md", "# Setup\nInstall the app.");
        await _server.Indexer.IndexFolderAsync(_server.Docs.Path);
        await _server.Indexer.RemoveAsync(_server.Docs.Path);

        var report = await _server.Indexer.SyncFolderAsync(_server.Docs.Path);

        Assert.Null(report);
        Assert.Empty(await _server.Store.ListSourcePathsAsync());
    }

    [Fact]
    public void A_subfolder_is_covered_by_its_watched_parent()
    {
        var subfolder = PathUtils.Normalize(Directory.CreateDirectory(_server.Docs.Combine("sub")).FullName);

        _server.Watcher.Watch(subfolder);
        _server.Watcher.Watch(_server.Docs.Path);
        _server.Watcher.Watch(subfolder);

        Assert.Equal([_server.Docs.Path], _server.Watcher.WatchedFolders);
        Assert.True(_server.Watcher.IsWatching(subfolder));
    }

    [Fact]
    public async Task Nothing_is_watched_when_watching_is_turned_off()
    {
        using var server = new TestServer(watch: false);
        server.Docs.Write("guide.md", "# Setup\nInstall the app.");

        var result = await server.Tools.IndexFolderAsync(server.Docs.Path);

        Assert.DoesNotContain("watched", result);
        Assert.Empty(server.Watcher.WatchedFolders);
    }

    [Fact]
    public async Task Watched_folders_are_listed_with_the_sources()
    {
        _server.Docs.Write("guide.md", "# Setup\nInstall the app.");
        await _server.Tools.IndexFolderAsync(_server.Docs.Path);

        var result = await _server.Tools.ListSourcesAsync();

        Assert.Contains($"Watched folders (changes are indexed automatically):\n- {_server.Docs.Path}", result);
    }

    [Theory]
    [InlineData("guide.md", true)]
    [InlineData("manual.PDF", true)]
    [InlineData("notes", true)]
    [InlineData("index.db", false)]
    [InlineData("index.db-wal", false)]
    [InlineData("Program.cs", false)]
    [InlineData(".git", false)]
    [InlineData(".git/index", false)]
    [InlineData("node_modules/package/readme.md", false)]
    public void Only_changes_that_can_affect_the_index_start_a_sync(string relativePath, bool expected)
    {
        var path = _server.Docs.Combine(relativePath.Split('/'));

        Assert.Equal(expected, FolderWatcher.IsRelevant(_server.Docs.Path, path));
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The index wasn't updated in time.");
            await Task.Delay(100);
        }
    }
}
