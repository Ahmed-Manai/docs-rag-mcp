using ModelContextProtocol;

namespace DocsRagMcp.Tests;

public sealed class DocsToolsTests : IDisposable
{
    private readonly TestServer _server = new();

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task Search_finds_the_relevant_passage_and_cites_its_file_and_section()
    {
        _server.Docs.Write(
            "billing.md",
            "# Refunds\nRefunds are paid back within five business days.\n\n# Invoices\nInvoices are sent on the first of each month.");
        _server.Docs.Write("travel.md", "# Hotels\nBook hotels through the travel portal.");
        await _server.Tools.IndexFolderAsync(_server.Docs.Path);

        var result = await _server.Tools.SearchDocsAsync("how many days until refunds are paid", topK: 1);

        Assert.Contains($"{_server.Docs.Combine("billing.md")} > Refunds", result);
        Assert.Contains("five business days", result);
    }

    [Fact]
    public async Task Search_on_an_empty_index_explains_what_to_do()
    {
        var result = await _server.Tools.SearchDocsAsync("anything");

        Assert.Contains("index_folder", result);
    }

    [Fact]
    public async Task Indexing_outside_the_allowed_paths_returns_a_clear_error()
    {
        using var server = new TestServer(allowedPaths: [Path.Combine(Path.GetTempPath(), "only-this-folder")]);

        var error = await Assert.ThrowsAsync<McpException>(() => server.Tools.IndexFolderAsync(server.Docs.Path));

        Assert.Contains("DOCSRAG_ALLOWED_PATHS", error.Message);
    }

    [Fact]
    public async Task Index_folder_reports_what_changed()
    {
        _server.Docs.Write("guide.md", "# Setup\nInstall the app.");

        var result = await _server.Tools.IndexFolderAsync(_server.Docs.Path);

        Assert.Contains("1 added, 0 updated, 0 unchanged, 0 removed", result);
    }

    [Fact]
    public async Task Sources_can_be_listed_and_removed()
    {
        var guide = _server.Docs.Write("guide.md", "# Setup\nInstall the app.");
        await _server.Tools.IndexFolderAsync(_server.Docs.Path);

        Assert.Contains(guide, await _server.Tools.ListSourcesAsync());
        Assert.Contains("Removed", await _server.Tools.RemoveFromIndexAsync(guide));
        Assert.Contains("empty", await _server.Tools.ListSourcesAsync());
    }
}
