using DocsRagMcp;
using DocsRagMcp.Embeddings;
using DocsRagMcp.Indexing;
using DocsRagMcp.Ingestion;
using DocsRagMcp.Storage;
using DocsRagMcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(_ => EmbeddingGeneratorFactory.Create(options));
builder.Services.AddSingleton(_ => new ChunkStore(options.DatabasePath));
builder.Services.AddSingleton(_ => new TextChunker(options.ChunkSize, options.ChunkOverlap));
builder.Services.AddSingleton(_ => new PathGuard(options.AllowedPaths));
builder.Services.AddSingleton<DocumentIndexer>();

builder.Services
    .AddMcpServer(server => server.ServerInstructions =
        "Use search_docs to answer questions from the user's own documents and cite the files you used. " +
        "If the index is empty, ask which folder to index and call index_folder.")
    .WithStdioServerTransport()
    .WithTools<DocsTools>();

var app = builder.Build();
app.Services.GetRequiredService<ILogger<DocsRagOptions>>().LogInformation(
    "docs-rag-mcp is using {Model}, {SearchMode} search and the index at {Database}",
    options.EmbeddingModelId,
    options.SearchMode.ToString().ToLowerInvariant(),
    options.DatabasePath);

await app.RunAsync();
return 0;
