#:package ModelContextProtocol@2.4.0

// End-to-end check against a real embedding model: starts the server the way an MCP client does,
// indexes the sample handbook and asks a question.
//
//   dotnet build
//   dotnet run samples/smoke.cs -- "How much can I spend on a hotel in Paris?"
//
// Uses Ollama by default; set the DOCSRAG_* variables to try another provider.

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

var repository = Directory.GetCurrentDirectory();
var database = Path.Combine(Path.GetTempPath(), $"docs-rag-smoke-{Guid.NewGuid():N}.db");
var question = args.Length > 0 ? string.Join(' ', args) : "How much can I spend on a hotel in Paris?";

await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "docs-rag-mcp",
    Command = "dotnet",
    Arguments = ["run", "--no-build", "--project", Path.Combine(repository, "src", "DocsRagMcp"), "--", "--database", database],
}));

try
{
    Console.WriteLine(await CallAsync("index_folder", new() { ["path"] = Path.Combine(repository, "samples", "handbook") }));
    Console.WriteLine(await CallAsync("search_docs", new() { ["query"] = question, ["topK"] = 2 }));
}
finally
{
    File.Delete(database);
}

async Task<string> CallAsync(string tool, Dictionary<string, object?> arguments)
{
    var result = await client.CallToolAsync(tool, arguments);
    var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(block => block.Text));
    return result.IsError == true ? $"{tool} failed: {text}\n" : $"> {tool}\n{text}\n";
}
