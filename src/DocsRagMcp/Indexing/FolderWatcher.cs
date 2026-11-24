using DocsRagMcp.Ingestion;
using DocsRagMcp.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DocsRagMcp.Indexing;

/// <summary>
/// Keeps indexed folders in sync while the server runs. Every folder indexed with <c>index_folder</c>
/// is remembered in the index and watched. A burst of changes, such as an editor saving or a
/// <c>git pull</c>, is collected until the folder has been quiet for a moment and then synced in one
/// run, which only re-embeds the files whose content changed. When the server starts, remembered
/// folders are synced once to pick up what changed while it was stopped.
/// </summary>
public sealed class FolderWatcher(
    DocumentIndexer indexer,
    ChunkStore store,
    DocsRagOptions options,
    ILogger<FolderWatcher> logger) : BackgroundService
{
    private readonly Dictionary<string, FolderWatch> _watches = new(PathUtils.Comparer);
    private readonly CancellationTokenSource _stopping = new();
    private bool _disposed;

    /// <summary>How long a folder must be quiet after a change before it is synced.</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.FromSeconds(2);

    public bool Enabled => options.Watch;

    public IReadOnlyList<string> WatchedFolders
    {
        get
        {
            lock (_watches)
            {
                return _watches.Keys.Order(PathUtils.Comparer).ToList();
            }
        }
    }

    /// <summary>True when <paramref name="path"/> is a watched folder or inside one.</summary>
    public bool IsWatching(string path)
    {
        lock (_watches)
        {
            return _watches.Keys.Any(folder => PathUtils.IsSameOrUnder(path, folder));
        }
    }

    /// <summary>Starts watching a folder, unless watching is turned off or a parent folder is already watched.</summary>
    public void Watch(string folder)
    {
        if (!Enabled)
        {
            return;
        }

        lock (_watches)
        {
            if (_disposed || _watches.Keys.Any(watched => PathUtils.IsSameOrUnder(folder, watched)))
            {
                return;
            }

            FolderWatch watch;
            try
            {
                watch = new FolderWatch(this, folder);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Can't watch {Folder}", folder);
                return;
            }

            // The new watch covers the subfolders, so their own watches are no longer needed.
            foreach (var subfolder in _watches.Keys.Where(watched => PathUtils.IsSameOrUnder(watched, folder)).ToList())
            {
                _watches.Remove(subfolder, out var replaced);
                replaced!.Dispose();
            }

            _watches[folder] = watch;
        }

        logger.LogInformation("Watching {Folder} for changes", folder);
    }

    /// <summary>Stops watching a folder and every watched folder inside it. Returns the folders that were watched.</summary>
    public IReadOnlyList<string> Unwatch(string pathOrFolder)
    {
        lock (_watches)
        {
            var stopped = _watches.Keys.Where(folder => PathUtils.IsSameOrUnder(folder, pathOrFolder)).ToList();
            foreach (var folder in stopped)
            {
                _watches.Remove(folder, out var watch);
                watch!.Dispose();
            }

            return stopped;
        }
    }

    /// <summary>
    /// True for changes that can affect the index: supported documents, and folders, since a folder
    /// that is renamed or deleted takes its files with it. Changes inside hidden and skipped folders,
    /// and to other files such as the index database itself, are ignored.
    /// </summary>
    public static bool IsRelevant(string folder, string path)
    {
        var relative = Path.GetRelativePath(folder, path);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            // The watcher reported a path in an unexpected form. A sync finds out what changed, so run one.
            return true;
        }

        var parts = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var name = parts[^1];

        if (parts[..^1].Any(DocumentIndexer.IsSkippedFolder))
        {
            return false;
        }

        // A deleted folder no longer exists, so a name without an extension is taken for one.
        return DocumentReader.IsSupported(name)
            || (!DocumentIndexer.IsSkippedFolder(name) && (!Path.HasExtension(name) || Directory.Exists(path)));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        lock (_watches)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var watch in _watches.Values)
            {
                watch.Dispose();
            }

            _watches.Clear();
        }

        // Not disposed: a sync that is already running may still read its token.
        _stopping.Cancel();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            return;
        }

        // Catching up can take a while, and must not hold up the start of the MCP server.
        await Task.Yield();

        IReadOnlyList<string> folders;
        try
        {
            folders = await store.ListFoldersAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Can't read the indexed folders, so none are watched");
            return;
        }

        // Sorted by path, so a folder comes before its subfolders, which its watch and sync cover.
        foreach (var folder in folders)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            if (!IsWatching(folder))
            {
                Watch(folder);
                await SyncAsync(folder);
            }
        }
    }

    private async Task SyncAsync(string folder)
    {
        var cancellationToken = _stopping.Token;
        try
        {
            var report = await indexer.SyncFolderAsync(folder, cancellationToken);
            if (report is not null && report.Added + report.Updated + report.Removed + report.Failed.Count > 0)
            {
                logger.LogInformation(
                    "Synced {Folder}: {Added} added, {Updated} updated, {Removed} removed, {Failed} could not be read",
                    folder,
                    report.Added,
                    report.Updated,
                    report.Removed,
                    report.Failed.Count);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException)
        {
            // The folder is kept in the index, so it is watched again the next time the server starts
            // if it's back by then (a network share or a USB drive, for example).
            logger.LogWarning("Stopped watching {Folder}: {Reason}", folder, ex.Message);
            Unwatch(folder);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't sync {Folder}; it will be tried again on its next change", folder);
        }
    }

    private void LogMissedChanges(string folder, Exception exception) =>
        logger.LogWarning(exception, "Missed some changes in {Folder}; syncing all of it", folder);

    /// <summary>A file system watcher for one folder, and the timer that waits for it to be quiet.</summary>
    private sealed class FolderWatch : IDisposable
    {
        private readonly FolderWatcher _owner;
        private readonly FileSystemWatcher _watcher;
        private readonly Timer _timer;

        public FolderWatch(FolderWatcher owner, string folder)
        {
            _owner = owner;
            _timer = new Timer(_ => _ = owner.SyncAsync(folder), null, Timeout.Infinite, Timeout.Infinite);
            _watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };

            void OnChanged(object sender, FileSystemEventArgs e)
            {
                if (IsRelevant(folder, e.FullPath))
                {
                    Schedule();
                }
            }

            _watcher.Created += OnChanged;
            _watcher.Changed += OnChanged;
            _watcher.Deleted += OnChanged;
            _watcher.Renamed += (_, e) =>
            {
                if (IsRelevant(folder, e.FullPath) || IsRelevant(folder, e.OldFullPath))
                {
                    Schedule();
                }
            };

            // Raised when too many changes arrive at once for the watcher to report them all.
            _watcher.Error += (_, e) =>
            {
                owner.LogMissedChanges(folder, e.GetException());
                Schedule();
            };

            _watcher.EnableRaisingEvents = true;
        }

        public void Dispose()
        {
            _watcher.Dispose();
            _timer.Dispose();
        }

        private void Schedule()
        {
            try
            {
                _timer.Change(_owner.Delay, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // The folder stopped being watched while the change was being reported.
            }
        }
    }
}
