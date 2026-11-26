using System.ComponentModel;
using System.Globalization;
using System.Text;
using DocsRagMcp.Indexing;
using DocsRagMcp.Reranking;
using DocsRagMcp.Resources;
using DocsRagMcp.Storage;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace DocsRagMcp.Tools;

/// <summary>The MCP tools that assistants such as Claude, GitHub Copilot and Cursor can call.</summary>
[McpServerToolType]
public sealed class DocsTools(
    DocumentIndexer indexer,
    FolderWatcher watcher,
    ChunkStore store,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    Reranker reranker,
    DocsRagOptions options)
{
    private const int MaxResults = 20;

    [McpServerTool(Name = "index_folder", Idempotent = true)]
    [Description(
        "Indexes the documents in a folder and its subfolders (Markdown, text, HTML, Word and PDF files) so they can be searched with search_docs. " +
        "Files that haven't changed since the last run are skipped, and files that were deleted are removed from the index. " +
        "Unless watching is turned off, the folder is then watched and changes to its files are indexed automatically.")]
    public Task<string> IndexFolderAsync(
        [Description("Absolute path of the folder to index.")] string path,
        CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var report = await indexer.IndexFolderAsync(path, cancellationToken);
            watcher.Watch(report.Folder);

            var text = FormatReport(report);
            return watcher.IsWatching(report.Folder)
                ? text + "\nThe folder is watched: changes to its files are indexed automatically."
                : text;
        });

    [McpServerTool(Name = "search_docs", ReadOnly = true)]
    [Description(
        "Searches the indexed documents by meaning and by keywords, and returns the most relevant passages with their file and section. " +
        "Use it to answer questions from the user's own documents, and cite the files you used.")]
    public Task<string> SearchDocsAsync(
        [Description("What to look for, as a question or a short phrase.")] string query,
        [Description("How many passages to return, from 1 to 20.")] int topK = 5,
        [Description(
            "Optional. Only search this part of the index: an absolute path of a folder or file, a file:// resource URI, " +
            "or a path relative to an indexed folder, such as \"api/v2\".")]
        string? folder = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                throw new ArgumentException("The query can't be empty.");
            }

            await store.CheckEmbeddingModelAsync(options.EmbeddingModelId, claimIfUnset: false, cancellationToken);

            var sources = await store.ListSourcePathsAsync(cancellationToken);
            if (sources.Count == 0)
            {
                return "The index is empty. Index a folder first with the index_folder tool.";
            }

            IReadOnlyList<string>? within = null;
            if (!string.IsNullOrWhiteSpace(folder))
            {
                within = await ResolveSearchScopeAsync(folder, cancellationToken);
                if (!sources.Any(source => within.Any(scope => PathUtils.IsSameOrUnder(source, scope))))
                {
                    return $"No indexed files are inside {folder}. Use list_sources to see the indexed files.";
                }
            }

            topK = Math.Clamp(topK, 1, MaxResults);
            var vector = await embeddingGenerator.GenerateVectorAsync(query, cancellationToken: cancellationToken);
            var keywords = options.SearchMode == SearchMode.Hybrid ? query : null;
            var hits = await store.SearchAsync(
                vector, topK, within, keywords, limit: reranker.CandidatesFor(topK), cancellationToken);

            // With a reranker, the best candidates are reordered by a cross-encoder model. Without
            // one, or when it fails, the search order is kept.
            hits = await reranker.RerankAsync(query, hits, topK, cancellationToken);

            return hits.Count == 0 ? "No matching passages found." : FormatHits(query, hits);
        });

    /// <summary>
    /// Turns the folder filter of search_docs into full paths. A relative path is looked up in every
    /// indexed folder, so "api" matches the "api" subfolder of each of them.
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveSearchScopeAsync(string folder, CancellationToken cancellationToken)
    {
        folder = folder.Trim();

        if (DocsResources.TryGetPath(folder, out var fromUri))
        {
            return [fromUri];
        }

        if (folder.StartsWith('~') || Path.IsPathRooted(Environment.ExpandEnvironmentVariables(folder)))
        {
            return [PathUtils.Normalize(folder)];
        }

        return (await store.ListFoldersAsync(cancellationToken))
            .Select(root => (Root: root, Scope: PathUtils.Normalize(Path.Combine(root, folder))))
            .Where(pair => PathUtils.IsSameOrUnder(pair.Scope, pair.Root))
            .Select(pair => pair.Scope)
            .Distinct(PathUtils.Comparer)
            .ToList();
    }

    [McpServerTool(Name = "list_sources", ReadOnly = true)]
    [Description("Shows where the index is and which embedding model built it, then lists the files in the index, with their number of chunks and when they were last indexed, and the watched folders.")]
    public Task<string> ListSourcesAsync(CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var text = new StringBuilder(await DescribeIndexAsync(cancellationToken)).Append("\n\n");

            var sources = await store.ListSourcesAsync(cancellationToken);
            if (sources.Count == 0)
            {
                return text.Append("The index is empty. Index a folder with the index_folder tool.").ToString();
            }

            text.Append(CultureInfo.InvariantCulture, $"{sources.Count} indexed files ({sources.Sum(s => s.ChunkCount)} chunks):\n");
            foreach (var source in sources)
            {
                text.Append(CultureInfo.InvariantCulture, $"- {source.Path} ({source.ChunkCount} chunks, indexed {source.IndexedAt:yyyy-MM-dd HH:mm} UTC)\n");
            }

            var watched = watcher.WatchedFolders;
            if (watched.Count > 0)
            {
                text.Append("\nWatched folders (changes are indexed automatically):\n");
                foreach (var folder in watched)
                {
                    text.Append("- ").Append(folder).Append('\n');
                }
            }

            return text.ToString().TrimEnd();
        });

    /// <summary>Where the index is, how big it is and which embedding model built it, to help when results look wrong.</summary>
    private async Task<string> DescribeIndexAsync(CancellationToken cancellationToken)
    {
        var file = new FileInfo(options.DatabasePath);
        var size = file.Exists ? $" ({FormatSize(file.Length)})" : string.Empty;

        var model = await store.GetEmbeddingModelAsync(cancellationToken);
        var builtWith = model is null
            ? $"will be built with {options.EmbeddingModelId}"
            : $"built with {model}";

        return $"Index: {options.DatabasePath}{size}, {builtWith}";
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MB"),
    };

    [McpServerTool(Name = "remove_from_index", Destructive = true, Idempotent = true)]
    [Description(
        "Removes a file, or every file inside a folder, from the index, and stops watching the folder. " +
        "Files on disk are not touched.")]
    public Task<string> RemoveFromIndexAsync(
        [Description("Absolute path of the file or folder to remove from the index.")] string path,
        CancellationToken cancellationToken = default) =>
        RunAsync(async () =>
        {
            var target = PathUtils.Normalize(path);

            // Stop watching first, so no new sync starts while the files are being removed.
            watcher.Unwatch(target);
            var removed = await indexer.RemoveAsync(target, cancellationToken);

            if (removed.Files.Count == 0 && removed.Folders.Count == 0)
            {
                return $"Nothing in the index matches {path}.";
            }

            var text = new StringBuilder(removed.Files.Count switch
            {
                0 => "No files were indexed there.",
                1 => $"Removed {removed.Files[0]} from the index.",
                _ => string.Create(CultureInfo.InvariantCulture, $"Removed {removed.Files.Count} files from the index."),
            });

            if (removed.Folders.Count > 0)
            {
                text.Append(" Stopped watching ").AppendJoin(", ", removed.Folders).Append('.');
            }

            if (watcher.IsWatching(target))
            {
                text.Append(
                    " It is inside a watched folder, so it is indexed again the next time it changes. " +
                    "To keep it out, delete it or remove the whole folder from the index.");
            }

            return text.ToString();
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
