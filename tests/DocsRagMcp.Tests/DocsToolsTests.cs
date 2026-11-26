using DocsRagMcp.Resources;
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

    [Theory]
    [InlineData("absolute")]
    [InlineData("relative")]
    [InlineData("uri")]
    [InlineData("file")]
    public async Task Search_can_be_limited_to_part_of_the_index(string form)
    {
        _server.Docs.Write("api/v1/auth.md", "# Tokens\nTokens expire after one hour.");
        var v2 = _server.Docs.Write("api/v2/auth.md", "# Tokens\nTokens expire after one day.");
        _server.Docs.Write("guides/auth.md", "# Tokens\nTokens expire when you sign out.");
        await _server.Tools.IndexFolderAsync(_server.Docs.Path);

        var folder = form switch
        {
            "absolute" => _server.Docs.Combine("api", "v2"),
            "relative" => "api/v2",
            "uri" => DocsResources.ToUri(_server.Docs.Combine("api", "v2")),
            _ => v2,
        };

        var result = await _server.Tools.SearchDocsAsync("when do tokens expire", topK: 5, folder: folder);

        Assert.StartsWith("1 passages", result);
        Assert.Contains($"{v2} > Tokens", result);
    }

    [Fact]
    public async Task Search_in_a_folder_with_no_indexed_files_says_so()
    {
        _server.Docs.Write("guide.md", "# Setup\nInstall the app.");
        await _server.Tools.IndexFolderAsync(_server.Docs.Path);
        var calls = _server.Embeddings.Calls;

        var result = await _server.Tools.SearchDocsAsync("install", folder: "no-such-folder");

        Assert.Equal("No indexed files are inside no-such-folder. Use list_sources to see the indexed files.", result);
        Assert.Equal(calls, _server.Embeddings.Calls);
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
    public async Task List_sources_starts_with_the_index_file_and_its_embedding_model()
    {
        var empty = await _server.Tools.ListSourcesAsync();
        Assert.StartsWith($"Index: {_server.Options.DatabasePath} (", empty);
        Assert.Contains($"will be built with {_server.Options.EmbeddingModelId}", empty);
        Assert.Contains("The index is empty", empty);

        _server.Docs.Write("guide.md", "# Setup\nInstall the app.");
        await _server.Tools.IndexFolderAsync(_server.Docs.Path);

        var listed = await _server.Tools.ListSourcesAsync();
        Assert.StartsWith($"Index: {_server.Options.DatabasePath} (", listed.Split('\n')[0]);
        Assert.EndsWith($"KB), built with {_server.Options.EmbeddingModelId}", listed.Split('\n')[0]);
        Assert.Contains("1 indexed files", listed);
    }

    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1023, "1023 bytes")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(13_002_342, "12.4 MB")]
    public void Index_size_is_readable(long bytes, string expected) =>
        Assert.Equal(expected, DocsRagMcp.Tools.DocsTools.FormatSize(bytes));

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
