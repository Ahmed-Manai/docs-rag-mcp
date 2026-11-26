using DocsRagMcp;
using DocsRagMcp.Embeddings;
using DocsRagMcp.Indexing;
using DocsRagMcp.Ingestion;
using DocsRagMcp.Resources;
using DocsRagMcp.Storage;
using DocsRagMcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

DocsRagOptions options;
try
{
    options = DocsRagOptions.FromConfiguration(DocsRagOptions.BuildConfiguration(args));
}
catch (DocsRagConfigurationException ex)
{
    await Console.Error.WriteLineAsync($"docs-rag-mcp: {ex.Message}");
    return 1;
}

var builder = Host.CreateApplicationBuilder();

// stdout carries the MCP protocol, so every log line goes to stderr.
builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);

// Every indexed file is listed as a resource. The collection is kept in step with the index, and
// the MCP SDK tells clients when it changes.
var resources = new McpServerResourceCollection();

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(_ => EmbeddingGeneratorFactory.Create(options));
builder.Services.AddSingleton(_ => new ChunkStore(options.DatabasePath));
builder.Services.AddSingleton(_ => new TextChunker(options.ChunkSize, options.ChunkOverlap));
builder.Services.AddSingleton(_ => new PathGuard(options.AllowedPaths));
builder.Services.AddSingleton<DocumentIndexer>();
builder.Services.AddSingleton<FolderWatcher>();
builder.Services.AddSingleton(resources);
builder.Services.AddSingleton<DocsResources>();
builder.Services.AddHostedService(services => services.GetRequiredService<FolderWatcher>());

builder.Services
    .AddMcpServer(server =>
    {
        server.ServerInstructions =
            "Use search_docs to answer questions from the user's own documents and cite the files you used. " +
            "If the index is empty, ask which folder to index and call index_folder. " +
            "Every indexed file is also a resource with a file:// URI, which can be read to get the whole document.";
        server.ResourceCollection = resources;
    })
    .WithStdioServerTransport()
    .WithTools<DocsTools>()
    // URIs that aren't in the collection (yet) are looked up in the index, which explains what's wrong.
    .WithReadResourceHandler((request, cancellationToken) =>
        new(request.Services!.GetRequiredService<DocsResources>().ReadAsync(request.Params?.Uri ?? string.Empty, cancellationToken)));

var app = builder.Build();

// Fill the resource list before the folder watcher starts, so it doesn't miss a change.
await app.Services.GetRequiredService<DocsResources>().RefreshAsync();

app.Services.GetRequiredService<ILogger<DocsRagOptions>>().LogInformation(
    "docs-rag-mcp is using {Model}, {SearchMode} search and the index at {Database}; folder watching is {Watch}",
    options.EmbeddingModelId,
    options.SearchMode.ToString().ToLowerInvariant(),
    options.DatabasePath,
    options.Watch ? "on" : "off");

await app.RunAsync();
return 0;
