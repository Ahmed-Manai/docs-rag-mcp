using DocsRagMcp.Storage;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DocsRagMcp.Tests;

/// <summary>Starts the real server process and talks to it over stdio, the way Claude or VS Code does.</summary>
public sealed class McpServerTests : IDisposable
{
    private readonly TempFolder _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public async Task Server_exposes_its_tools_over_stdio()
    {
        await using var client = await StartServerAsync();

        var tools = await client.ListToolsAsync();

        Assert.Equal(
            ["index_folder", "list_sources", "remove_from_index", "search_docs"],
            tools.Select(tool => tool.Name).Order());
        Assert.True(tools.Single(tool => tool.Name == "search_docs").ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.True(tools.Single(tool => tool.Name == "remove_from_index").ProtocolTool.Annotations?.DestructiveHint);
    }

    [Fact]
    public async Task Tool_call_round_trips_through_the_server()
    {
        await using var client = await StartServerAsync();

        var result = await client.CallToolAsync("list_sources");

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("The index is empty", text);
    }

    [Fact]
    public async Task Indexed_files_are_listed_and_read_as_resources()
    {
        using var docs = new TempFolder();
        var guide = docs.Write("setup.md", "# Setup\nInstall the app.\n");
        var store = new ChunkStore(_data.Combine("index.db"));
        await store.ReplaceSourceAsync(guide, "hash", [new ChunkToStore(0, "Setup", "Install the app.", FakeEmbeddingGenerator.Embed("Install the app."))]);

        await using var client = await StartServerAsync();
        Assert.True(client.ServerCapabilities.Resources?.ListChanged);
        Assert.NotNull(client.ServerCapabilities.Tools);

        var resource = Assert.Single(await client.ListResourcesAsync());
        Assert.Equal(new Uri(guide).AbsoluteUri, resource.Uri);
        Assert.Equal("text/markdown", resource.MimeType);

        var read = await client.ReadResourceAsync(resource.Uri);
        var contents = Assert.IsType<TextResourceContents>(Assert.Single(read.Contents));
        Assert.Equal("# Setup\nInstall the app.\n", contents.Text);
        Assert.Equal("text/markdown", contents.MimeType);

        var error = await Assert.ThrowsAsync<McpProtocolException>(
            () => client.ReadResourceAsync(new Uri(docs.Combine("not-indexed.md")).AbsoluteUri).AsTask());
        Assert.Contains("is not an indexed file", error.Message);
    }

    [Fact]
    public async Task Clients_are_told_when_the_indexed_files_change()
    {
        using var docs = new TempFolder();
        var guide = docs.Write("setup.md", "# Setup\nInstall the app.");
        var store = new ChunkStore(_data.Combine("index.db"));
        await store.ReplaceSourceAsync(guide, "hash", [new ChunkToStore(0, "Setup", "Install the app.", FakeEmbeddingGenerator.Embed("Install the app."))]);

        // Clients that use the initialize handshake get a session-wide notification. Newer clients
        // (protocol 2026-07-28) ask for it with subscriptions/listen, which the MCP SDK handles.
        await using var client = await StartServerAsync(new McpClientOptions { ProtocolVersion = "2025-11-25" });
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = client.RegisterNotificationHandler(
            NotificationMethods.ResourceListChangedNotification,
            (_, _) =>
            {
                changed.TrySetResult();
                return default;
            });

        await client.CallToolAsync("remove_from_index", new Dictionary<string, object?> { ["path"] = guide });

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(await client.ListResourcesAsync());
    }

    private Task<McpClient> StartServerAsync(McpClientOptions? clientOptions = null) =>
        McpClient.CreateAsync(
            new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "docs-rag-mcp",
                Command = "dotnet",
                Arguments = [Path.Combine(AppContext.BaseDirectory, "DocsRagMcp.dll"), "--database", _data.Combine("index.db")],
            }),
            clientOptions);
}
