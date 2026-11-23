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

    private Task<McpClient> StartServerAsync() =>
        McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "docs-rag-mcp",
            Command = "dotnet",
            Arguments = [Path.Combine(AppContext.BaseDirectory, "DocsRagMcp.dll"), "--database", _data.Combine("index.db")],
        }));
}
