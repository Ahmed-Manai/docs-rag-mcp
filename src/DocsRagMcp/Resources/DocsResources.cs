using System.Globalization;
using System.Text;
using DocsRagMcp.Indexing;
using DocsRagMcp.Ingestion;
using DocsRagMcp.Storage;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DocsRagMcp.Resources;

/// <summary>
/// Exposes the indexed files as MCP resources, so a client can list them and attach a whole
/// document to a conversation. Each file is identified by its <c>file://</c> URI. The resources live
/// in the server's resource collection, which is kept in step with the index; the MCP SDK then
/// tells clients when the list changes. Only files that are in the index can be read, and only
/// while they are still inside the allowed folders.
/// </summary>
public sealed class DocsResources
{
    private static readonly HashSet<string> MarkdownExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".mdx" };

    private static readonly HashSet<string> RawTextExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".mdx", ".txt", ".rst", ".adoc" };

    private readonly ChunkStore _store;
    private readonly PathGuard _pathGuard;
    private readonly ILogger<DocsResources> _logger;

    // One refresh at a time, so two quick changes can't interleave their updates of the collection.
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public DocsResources(
        ChunkStore store,
        PathGuard pathGuard,
        DocumentIndexer indexer,
        McpServerResourceCollection collection,
        ILogger<DocsResources> logger)
    {
        _store = store;
        _pathGuard = pathGuard;
        _logger = logger;
        Collection = collection;
        // Off the indexer's thread, so updating the list never slows down or blocks indexing.
        indexer.SourcesChanged += () => _ = Task.Run(RefreshInBackgroundAsync);
    }

    /// <summary>The resources the server lists: one per indexed file.</summary>
    public McpServerResourceCollection Collection { get; }

    /// <summary>The resource URI of an indexed file, such as <c>file:///home/me/notes/setup.md</c>.</summary>
    public static string ToUri(string path) => new Uri(path, UriKind.Absolute).AbsoluteUri;

    /// <summary>Turns a <c>file://</c> URI back into a full path. Returns false for anything else.</summary>
    public static bool TryGetPath(string uri, out string path)
    {
        path = string.Empty;
        if (!uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ||
            !parsed.IsFile ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !string.IsNullOrEmpty(parsed.Fragment))
        {
            return false;
        }

        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parsed.LocalPath));
        return true;
    }

    /// <summary>
    /// Markdown and text files are served as they are on disk. HTML, Word and PDF files are served as
    /// the text that was extracted from them for the index, which is what an assistant can read.
    /// </summary>
    public static string GetMimeType(string path) =>
        MarkdownExtensions.Contains(Path.GetExtension(path)) ? "text/markdown" : "text/plain";

    /// <summary>
    /// Brings the resource collection in line with the index. Clients are notified once per refresh,
    /// and only when something changed.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var sources = await _store.ListSourcesAsync(cancellationToken);
            var folders = await _store.ListFoldersAsync(cancellationToken);
            var wanted = sources
                .Select(source => Describe(source, folders))
                .ToDictionary(resource => resource.Uri, StringComparer.Ordinal);

            using (Collection.DeferChangedEvents())
            {
                foreach (var existing in Collection.ToArray())
                {
                    if (existing.ProtocolResource is not { } current ||
                        !wanted.TryGetValue(current.Uri, out var resource) ||
                        current.Description != resource.Description)
                    {
                        Collection.Remove(existing);
                    }
                }

                foreach (var resource in wanted.Values)
                {
                    Collection.TryAdd(new IndexedFile(this, resource));
                }
            }
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task<ReadResourceResult> ReadAsync(string uri, CancellationToken cancellationToken = default)
    {
        if (!TryGetPath(uri, out var path) || await _store.GetContentHashAsync(path, cancellationToken) is null)
        {
            throw NotFound($"{uri} is not an indexed file. Use list_sources to see the indexed files.");
        }

        if (!_pathGuard.IsAllowed(path))
        {
            throw NotFound($"{path} is outside the allowed paths ({string.Join(", ", _pathGuard.AllowedRoots)}).");
        }

        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw NotFound($"{path} is in the index but no longer exists on disk.");
        }

        // The indexer never follows symbolic links, so a link that appeared after indexing isn't followed either.
        var folders = await _store.ListFoldersAsync(cancellationToken);
        if (IsBehindLink(file, IndexedFolderOf(path, folders)))
        {
            throw NotFound($"{path} is a symbolic link or inside one, and links are not followed.");
        }

        if (file.Length > DocumentIndexer.MaxFileBytes)
        {
            throw NotFound($"{path} is larger than {DocumentIndexer.MaxFileBytes / (1024 * 1024)} MB.");
        }

        var text = RawTextExtensions.Contains(file.Extension)
            ? await File.ReadAllTextAsync(path, cancellationToken)
            : FormatParts(DocumentReader.Read(path));

        return new ReadResourceResult
        {
            Contents = [new TextResourceContents { Uri = ToUri(path), MimeType = GetMimeType(path), Text = text }],
        };
    }

    /// <summary>Joins the parts of an HTML, Word or PDF document. PDF pages are marked, since their text doesn't say which page it is.</summary>
    public static string FormatParts(IReadOnlyList<DocumentPart> parts)
    {
        var text = new StringBuilder();
        foreach (var part in parts)
        {
            if (text.Length > 0)
            {
                text.Append("\n\n");
            }

            if (part.Location is not null && !part.Text.StartsWith(part.Location, StringComparison.Ordinal))
            {
                text.Append('[').Append(part.Location).Append("]\n");
            }

            text.Append(part.Text);
        }

        return text.ToString();
    }

    private async Task RefreshInBackgroundAsync()
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't update the list of resources");
        }
    }

    private static Resource Describe(SourceInfo source, IReadOnlyList<string> folders)
    {
        // Named after the indexed folder and the path inside it, such as "handbook/policies/travel.md".
        var folder = IndexedFolderOf(source.Path, folders);
        var name = folder is null
            ? Path.GetFileName(source.Path)
            : Path.GetRelativePath(Path.GetDirectoryName(folder) ?? folder, source.Path).Replace('\\', '/');

        return new Resource
        {
            Uri = ToUri(source.Path),
            Name = name,
            Title = Path.GetFileName(source.Path),
            Description = string.Create(
                CultureInfo.InvariantCulture,
                $"Indexed document {source.Path} ({source.ChunkCount} chunks, indexed {source.IndexedAt:yyyy-MM-dd HH:mm:ss} UTC)"),
            MimeType = GetMimeType(source.Path),
        };
    }

    /// <summary>The outermost indexed folder that holds the file, or null for indexes built before folders were remembered.</summary>
    private static string? IndexedFolderOf(string path, IReadOnlyList<string> folders) =>
        folders.Where(folder => PathUtils.IsSameOrUnder(path, folder)).MinBy(folder => folder.Length);

    private static bool IsBehindLink(FileInfo file, string? indexedFolder)
    {
        if (file.LinkTarget is not null)
        {
            return true;
        }

        if (indexedFolder is null)
        {
            return false;
        }

        for (var folder = file.Directory; folder is not null; folder = folder.Parent)
        {
            var fullName = Path.TrimEndingDirectorySeparator(folder.FullName);
            if (!PathUtils.IsSameOrUnder(fullName, indexedFolder) || fullName.Equals(indexedFolder, PathUtils.Comparison))
            {
                return false;
            }

            if (folder.LinkTarget is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static McpProtocolException NotFound(string message) => new(message, McpErrorCode.ResourceNotFound);

    /// <summary>One indexed file in the server's resource collection.</summary>
    private sealed class IndexedFile(DocsResources owner, Resource resource) : McpServerResource
    {
        public override ResourceTemplate ProtocolResourceTemplate { get; } = new()
        {
            UriTemplate = resource.Uri,
            Name = resource.Name,
            Title = resource.Title,
            Description = resource.Description,
            MimeType = resource.MimeType,
        };

        public override Resource ProtocolResource => resource;

        public override IReadOnlyList<object> Metadata => [];

        public override bool IsMatch(string uri) => uri.Equals(resource.Uri, StringComparison.Ordinal);

        public override async ValueTask<ReadResourceResult> ReadAsync(
            RequestContext<ReadResourceRequestParams> request, CancellationToken cancellationToken = default) =>
            await owner.ReadAsync(resource.Uri, cancellationToken);
    }
}
