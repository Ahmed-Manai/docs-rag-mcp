using System.ComponentModel;
using System.Globalization;
using System.Text;
using DocsRagMcp.Indexing;
using DocsRagMcp.Storage;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DocsRagMcp.Tools;

/// <summary>The MCP tools that assistants such as Claude, GitHub Copilot and Cursor can call.</summary>
[McpServerToolType]
public sealed class DocsTools(
    DocumentIndexer indexer,
    ChunkStore store,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    DocsRagOptions options)
{
    private const int MaxResults = 20;

    [McpServerTool(Name = "index_folder", Idempotent = true)]
    [Description(
        "Indexes the documents in a folder and its subfolders (Markdown, text and PDF files) so they can be searched with search_docs. " +
        "Files that haven't changed since the last run are skipped, and files that were deleted are removed from the index.")]
    public Task<string> IndexFolderAsync(
        [Description("Absolute path of the folder to index.")] string path,
        CancellationToken cancellationToken = default) =>
        RunAsync(async () => FormatReport(await indexer.IndexFolderAsync(path, cancellationToken)));

    [McpServerTool(Name = "search_docs", ReadOnly = true)]
    [Description(
        "Searches the indexed documents by meaning and by keywords, and returns the most relevant passages with their file and section. " +
        "Use it to answer questions from the user's own documents, and cite the files you used.")]
    public Task<string> SearchDocsAsync(
        [Description("What to look for, as a question or a short phrase.")] string query,
        [Description("How many passages to return, from 1 to 20.")] int topK = 5,
        [Description("Optional. Only search files inside this folder.")] string? folder = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                throw new ArgumentException("The query can't be empty.");
            }

            await store.CheckEmbeddingModelAsync(options.EmbeddingModelId, claimIfUnset: false, cancellationToken);

            if ((await store.ListSourcePathsAsync(cancellationToken)).Count == 0)
            {
                return "The index is empty. Index a folder first with the index_folder tool.";
            }

            var vector = await embeddingGenerator.GenerateVectorAsync(query, cancellationToken: cancellationToken);
            var within = string.IsNullOrWhiteSpace(folder) ? null : PathUtils.Normalize(folder);
            var keywords = options.SearchMode == SearchMode.Hybrid ? query : null;
            var hits = await store.SearchAsync(
                vector, Math.Clamp(topK, 1, MaxResults), within, keywords, cancellationToken);

            return hits.Count == 0 ? "No matching passages found." : FormatHits(query, hits);
        });

    [McpServerTool(Name = "list_sources", ReadOnly = true)]
    [Description("Lists the files in the index, with their number of chunks and when they were last indexed.")]
    public Task<string> ListSourcesAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var sources = await store.ListSourcesAsync(cancellationToken);
            if (sources.Count == 0)
            {
                return "The index is empty. Index a folder with the index_folder tool.";
            }

            var text = new StringBuilder($"{sources.Count} indexed files ({sources.Sum(s => s.ChunkCount)} chunks):\n");
            foreach (var source in sources)
            {
                text.Append(CultureInfo.InvariantCulture, $"- {source.Path} ({source.ChunkCount} chunks, indexed {source.IndexedAt:yyyy-MM-dd HH:mm} UTC)\n");
            }

            return text.ToString().TrimEnd();
        });

    [McpServerTool(Name = "remove_from_index", Destructive = true, Idempotent = true)]
    [Description("Removes a file, or every file inside a folder, from the index. Files on disk are not touched.")]
    public Task<string> RemoveFromIndexAsync(
        [Description("Absolute path of the file or folder to remove from the index.")] string path,
        CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var removed = await store.RemoveAsync(PathUtils.Normalize(path), cancellationToken);
            return removed.Count switch
            {
                0 => $"Nothing in the index matches {path}.",
                1 => $"Removed {removed[0]} from the index.",
                _ => $"Removed {removed.Count} files from the index.",
            };
        });

    public static string FormatReport(IndexReport report)
    {
        var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
            $"Indexed {report.Folder}: {report.Added} added, {report.Updated} updated, {report.Unchanged} unchanged, " +
            $"{report.Removed} removed ({report.Chunks} new chunks)."));

        if (report.Failed.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n{report.Failed.Count} files could not be read:");
            foreach (var failure in report.Failed)
            {
                text.Append("\n- ").Append(failure);
            }
        }

        return text.ToString();
    }

    public static string FormatHits(string query, IReadOnlyList<SearchHit> hits)
    {
        var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"{hits.Count} passages for \"{query}\":"));
        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            var location = hit.Location is null ? string.Empty : $" > {hit.Location}";
            text.Append(CultureInfo.InvariantCulture, $"\n\n[{i + 1}] {hit.Path}{location} (score {hit.Score:0.00})\n{hit.Text}");
        }

        return text.ToString();
    }

    /// <summary>
    /// Turns failures into messages the assistant can act on. Without this, the MCP SDK hides the
    /// reason behind a generic error, and the assistant can't tell the user what to fix.
    /// </summary>
    private async Task<string> RunAsync(Func<Task<string>> action)
    {
        try
        {
            return await action();
        }
        catch (HttpRequestException ex)
        {
            var hint = options.Provider == EmbeddingProvider.Ollama
                ? $" Check that Ollama is running at {options.Endpoint} and that the model is pulled (ollama pull {options.Model})."
                : string.Empty;
            throw new McpException($"Couldn't reach the embedding service: {ex.Message}.{hint}", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not McpException)
        {
            throw new McpException(ex.Message, ex);
        }
    }
}
