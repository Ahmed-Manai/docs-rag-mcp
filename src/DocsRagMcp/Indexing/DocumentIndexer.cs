using System.Security.Cryptography;
using DocsRagMcp.Ingestion;
using DocsRagMcp.Storage;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace DocsRagMcp.Indexing;

public sealed record IndexReport(
    string Folder,
    int Added,
    int Updated,
    int Unchanged,
    int Removed,
    int Chunks,
    IReadOnlyList<string> Failed);

/// <summary>
/// Keeps the index in sync with a folder: new and changed files are read, chunked and embedded,
/// unchanged files are skipped by content hash, and deleted files are removed.
/// </summary>
public sealed class DocumentIndexer(
    ChunkStore store,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    TextChunker chunker,
    PathGuard pathGuard,
    DocsRagOptions options,
    ILogger<DocumentIndexer> logger)
{
    private const int EmbeddingBatchSize = 32;
    private const long MaxFileBytes = 25 * 1024 * 1024;

    private static readonly HashSet<string> SkippedFolders =
        new(StringComparer.OrdinalIgnoreCase) { "node_modules", "bin", "obj", "packages", "__pycache__" };

    private static readonly EnumerationOptions ShallowEnumeration = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
    };

    // One indexing run at a time: two runs over the same folder would race on the same files.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IndexReport> IndexFolderAsync(string folder, CancellationToken cancellationToken = default)
    {
        var root = pathGuard.ResolveFolder(folder);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await store.CheckEmbeddingModelAsync(options.EmbeddingModelId, claimIfUnset: true, cancellationToken);

            int added = 0, updated = 0, unchanged = 0, chunkCount = 0;
            var failed = new List<string>();
            var found = new HashSet<string>(PathUtils.Comparer);

            foreach (var file in EnumerateDocuments(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                found.Add(file);

                string hash;
                string? previous;
                IReadOnlyList<(string? Location, string Text)> pieces;
                try
                {
                    hash = await HashFileAsync(file, cancellationToken);
                    previous = await store.GetContentHashAsync(file, cancellationToken);
                    if (previous == hash)
                    {
                        unchanged++;
                        continue;
                    }

                    pieces = ReadAndChunk(file);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A broken or locked file shouldn't stop the rest of the folder from being indexed.
                    logger.LogWarning(ex, "Skipping {File}", file);
                    failed.Add($"{file}: {ex.Message}");
                    continue;
                }

                var chunks = await EmbedAsync(file, pieces, cancellationToken);
                await store.ReplaceSourceAsync(file, hash, chunks, cancellationToken);

                chunkCount += chunks.Count;
                if (previous is null)
                {
                    added++;
                }
                else
                {
                    updated++;
                }

                logger.LogInformation("Indexed {File} ({Chunks} chunks)", file, chunks.Count);
            }

            var deleted = (await store.ListSourcePathsAsync(cancellationToken))
                .Where(path => PathUtils.IsSameOrUnder(path, root) && !found.Contains(path))
                .ToList();

            foreach (var path in deleted)
            {
                await store.RemoveAsync(path, cancellationToken);
            }

            return new IndexReport(root, added, updated, unchanged, deleted.Count, chunkCount, failed);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The text that gets embedded: the chunk prefixed with its file name and section,
    /// so a passage like "Run the installer" still matches a query about "setup on Windows".
    /// </summary>
    public static string ContextualText(string fileName, string? location, string text) =>
        location is null ? $"{fileName}\n\n{text}" : $"{fileName} > {location}\n\n{text}";

    private IReadOnlyList<(string? Location, string Text)> ReadAndChunk(string file) =>
        DocumentReader.Read(file)
            .SelectMany(part => chunker.Split(part.Text).Select(text => (part.Location, text)))
            .ToList();

    private async Task<IReadOnlyList<ChunkToStore>> EmbedAsync(
        string file, IReadOnlyList<(string? Location, string Text)> pieces, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(file);
        var chunks = new List<ChunkToStore>(pieces.Count);

        foreach (var batch in pieces.Chunk(EmbeddingBatchSize))
        {
            var inputs = batch.Select(piece => ContextualText(fileName, piece.Location, piece.Text));
            var embeddings = await embeddingGenerator.GenerateAsync(inputs, cancellationToken: cancellationToken);

            if (embeddings.Count != batch.Length)
            {
                throw new InvalidOperationException(
                    $"The embedding service returned {embeddings.Count} vectors for {batch.Length} chunks.");
            }

            for (var i = 0; i < batch.Length; i++)
            {
                chunks.Add(new ChunkToStore(chunks.Count, batch[i].Location, batch[i].Text, embeddings[i].Vector));
            }
        }

        return chunks;
    }

    private static IEnumerable<string> EnumerateDocuments(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var folder = pending.Pop();

            foreach (var subfolder in Directory.EnumerateDirectories(folder, "*", ShallowEnumeration).Order(StringComparer.Ordinal).Reverse())
            {
                var name = Path.GetFileName(subfolder);
                if (!name.StartsWith('.') && !SkippedFolders.Contains(name))
                {
                    pending.Push(subfolder);
                }
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*", ShallowEnumeration).Order(StringComparer.Ordinal))
            {
                if (DocumentReader.IsSupported(file) && new FileInfo(file).Length <= MaxFileBytes)
                {
                    yield return file;
                }
            }
        }
    }

    private static async Task<string> HashFileAsync(string file, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(file);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }
}
