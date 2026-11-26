using DocsRagMcp.Ingestion;
using DocsRagMcp.Resources;
using DocsRagMcp.Storage;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace DocsRagMcp.Tests;

public sealed class DocsResourcesTests : IDisposable
{
    private readonly TestServer _server = new(watch: false);

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task Indexed_files_are_listed_with_their_uri_name_and_type()
    {
        var guide = _server.Docs.Write("guides/setup.md", "# Setup\nInstall the app.");
        var page = _server.Docs.Write("site/index.html", "<h1>Home</h1><p>Welcome.</p>");
        await _server.Tools.IndexFolderAsync(_server.Docs.Path);

        var resources = await ListAsync();

        Assert.Equal(2, resources.Count);

        var markdown = resources.Single(resource => resource.Title == "setup.md");
        Assert.Equal(new Uri(guide).AbsoluteUri, markdown.Uri);
        Assert.StartsWith("file:///", markdown.Uri);
        Assert.Equal($"{Path.GetFileName(_server.Docs.Path)}/guides/setup.md", markdown.Name);
        Assert.Equal("text/markdown", markdown.MimeType);
        Assert.Contains("1 chunks", markdown.Description);

        var html = resources.Single(resource => resource.Title == "index.html");
        Assert.Equal(DocsResources.ToUri(page), html.Uri);
        Assert.Equal("text/plain", html.MimeType);
    }

    [Fact]
    public async Task Reading_a_markdown_file_returns_it_as_it_is_on_disk()
    {
        const string Content = "---\ntitle: Setup\n---\n# Setup\nInstall the app.\n";
        var guide = _server.Docs.Write("setup.md", Content);
        await _server.Tools.IndexFolderAsync(_server.Docs.Path);

        var result = await _server.Resources.ReadAsync(DocsResources.ToUri(guide));

        var contents = Assert.IsType<TextResourceContents>(Assert.Single(result.Contents));
        Assert.Equal(DocsResources.ToUri(guide), contents.Uri);
        Assert.Equal("text/markdown", contents.MimeType);
        Assert.Equal(Content, contents.Text);
    }

    [Fact]
    public async Task Reading_an_html_page_returns_its_indexed_text()
    {
        var page = _server.Docs.Write(
            "page.html",
            "<html><head><script>track()</script></head><body><nav>Menu</nav><h1>Refunds</h1><p>Paid within five days.</p></body></html>");
        await _server.Tools.IndexFolderAsync(_server.Docs.Path);

        var result = await _server.Resources.ReadAsync(DocsResources.ToUri(page));

        var contents = Assert.IsType<TextResourceContents>(Assert.Single(result.Contents));
        Assert.Equal("text/plain", contents.MimeType);
        Assert.Contains("Refunds", contents.Text);
        Assert.Contains("Paid within five days.", contents.Text);
        Assert.DoesNotContain("track()", contents.Text);
        Assert.DoesNotContain("Menu", contents.Text);
    }

    [Fact]
    public void Pdf_pages_are_marked_when_their_text_does_not_name_them()
    {
        var text = DocsResources.FormatParts(
        [
            new DocumentPart("First page text.", "page 1"),
            new DocumentPart("Second page text.", "page 2"),
        ]);

        Assert.Equal("[page 1]\nFirst page text.\n\n[page 2]\nSecond page text.", text);
        Assert.Equal("Intro\nText.", DocsResources.FormatParts([new DocumentPart("Intro\nText.", "Intro")]));
    }

    [Theory]
    [InlineData("my notes.md")]
    [InlineData("c#-and-f#.md")]
    [InlineData("100% done.md")]
    [InlineData("réunion d'équipe.md")]
    [InlineData("{draft} notes.md")]
    public async Task Uris_round_trip_for_unusual_file_names(string fileName)
    {
        var file = _server.Docs.Write(fileName, "# Notes\nSome text.");
        await _server.Tools.IndexFolderAsync(_server.Docs.Path);

        var uri = Assert.Single(await ListAsync()).Uri;

        Assert.True(DocsResources.TryGetPath(uri, out var path));
        Assert.Equal(file, path);
        var contents = Assert.IsType<TextResourceContents>(Assert.Single((await _server.Resources.ReadAsync(uri)).Contents));
        Assert.Contains("Some text.", contents.Text);
    }

    [Theory]
    [InlineData("https://example.com/notes.md")]
    [InlineData("docs://notes.md")]
    [InlineData("notes.md")]
    [InlineData("")]
    public void Only_file_uris_are_accepted(string uri) =>
        Assert.False(DocsResources.TryGetPath(uri, out _));

    [Fact]
    public async Task Files_that_are_not_indexed_cannot_be_read()
    {
        _server.Docs.Write("indexed.md", "# A\nText.");
        await _server.Tools.IndexFolderAsync(_server.Docs.Path);

        // Created after indexing, and the folder isn't watched: it is on disk but not in the index.
        var notIndexed = _server.Docs.Write("secret.md", "# Secret\nDon't read me.");
        var outside = Path.Combine(Path.GetTempPath(), "outside.md");

        var errors = new[]
        {
            await Assert.ThrowsAsync<McpProtocolException>(() => _server.Resources.ReadAsync(DocsResources.ToUri(notIndexed))),
            await Assert.ThrowsAsync<McpProtocolException>(() => _server.Resources.ReadAsync(DocsResources.ToUri(outside))),
            await Assert.ThrowsAsync<McpProtocolException>(() => _server.Resources.ReadAsync(
                DocsResources.ToUri(_server.Docs.Path) + "/../" + Path.GetFileName(_server.Docs.Path) + "/secret.md")),
            await Assert.ThrowsAsync<McpProtocolException>(() => _server.Resources.ReadAsync("https://example.com/indexed.md")),
        };

        Assert.All(errors, error =>
        {
            Assert.Equal(McpErrorCode.ResourceNotFound, error.ErrorCode);
            Assert.Contains("is not an indexed file", error.Message);
        });
    }

    [Fact]
    public async Task Indexed_files_outside_the_allowed_paths_are_not_served()
    {
        // An index built before DOCSRAG_ALLOWED_PATHS was narrowed still lists the file, but the server won't read it.
        using var server = new TestServer(allowedPaths: [Path.Combine(Path.GetTempPath(), "only-this-folder")], watch: false);
        var file = server.Docs.Write("notes.md", "# Notes\nPrivate.");
        await server.Store.ReplaceSourceAsync(file, "hash", [new ChunkToStore(0, null, "Private.", FakeEmbeddingGenerator.Embed("Private."))]);

        var error = await Assert.ThrowsAsync<McpProtocolException>(() => server.Resources.ReadAsync(DocsResources.ToUri(file)));

        Assert.Equal(McpErrorCode.ResourceNotFound, error.ErrorCode);
        Assert.Contains("outside the allowed paths", error.Message);
    }

    [Fact]
    public async Task Deleted_files_report_that_they_are_gone()
    {
        var file = _server.Docs.Write("gone.md", "# Gone\nText.");
        await _server.Tools.IndexFolderAsync(_server.Docs.Path);
        File.Delete(file);

        var error = await Assert.ThrowsAsync<McpProtocolException>(() => _server.Resources.ReadAsync(DocsResources.ToUri(file)));

        Assert.Contains("no longer exists", error.Message);
    }

    [Fact]
    public async Task A_folder_replaced_by_a_symbolic_link_after_indexing_is_not_followed()
    {
        var file = _server.Docs.Write("team/notes.md", "# Notes\nTeam notes.");
        await _server.Tools.IndexFolderAsync(_server.Docs.Path);

        using var elsewhere = new TempFolder();
        elsewhere.Write("notes.md", "# Notes\nSomething else entirely.");
        Directory.Delete(_server.Docs.Combine("team"), recursive: true);
        try
        {
            Directory.CreateSymbolicLink(_server.Docs.Combine("team"), elsewhere.Path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Creating links needs Developer Mode or admin rights on Windows.
            return;
        }

        var error = await Assert.ThrowsAsync<McpProtocolException>(() => _server.Resources.ReadAsync(DocsResources.ToUri(file)));

        Assert.Contains("links are not followed", error.Message);
    }

    [Fact]
    public async Task The_list_follows_the_index_and_changes_only_when_the_index_does()
    {
        var changes = 0;
        _server.Resources.Collection.Changed += (_, _) => changes++;
        var guide = _server.Docs.Write("guide.md", "# Setup\nInstall the app.");
        _server.Docs.Write("faq.md", "# FAQ\nAnswers.");
        await _server.Tools.IndexFolderAsync(_server.Docs.Path);

        Assert.Equal(2, (await ListAsync()).Count);
        Assert.Equal(1, changes);

        await _server.Tools.IndexFolderAsync(_server.Docs.Path);
        await ListAsync();
        Assert.Equal(1, changes);

        await _server.Tools.RemoveFromIndexAsync(guide);
        var resources = await ListAsync();
        Assert.Equal("faq.md", Assert.Single(resources).Title);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task Reading_a_pdf_returns_its_text_page_by_page()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(PageSize.A4).AddText("Invoices are due in 30 days.", 12, new PdfPoint(25, 700), font);
        builder.AddPage(PageSize.A4).AddText("Refunds take five business days.", 12, new PdfPoint(25, 700), font);
        var pdf = _server.Docs.Combine("policy.pdf");
        File.WriteAllBytes(pdf, builder.Build());
        await _server.Tools.IndexFolderAsync(_server.Docs.Path);

        var contents = Assert.IsType<TextResourceContents>(
            Assert.Single((await _server.Resources.ReadAsync(DocsResources.ToUri(pdf))).Contents));

        Assert.Equal("text/plain", contents.MimeType);
        Assert.Equal("text/plain", Assert.Single(await ListAsync()).MimeType);
        Assert.StartsWith("[page 1]\nInvoices are due in 30 days.", contents.Text);
        Assert.Contains("[page 2]\nRefunds take five business days.", contents.Text);
    }

    private async Task<List<Resource>> ListAsync()
    {
        await _server.Resources.RefreshAsync();
        return _server.Resources.Collection.Select(resource => resource.ProtocolResource!).ToList();
    }
}
