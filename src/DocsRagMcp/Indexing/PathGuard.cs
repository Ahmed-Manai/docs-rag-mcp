namespace DocsRagMcp.Indexing;

/// <summary>
/// Resolves the folders an assistant asks to index, and keeps indexing inside
/// <c>DOCSRAG_ALLOWED_PATHS</c> when that setting is used.
/// </summary>
public sealed class PathGuard(IEnumerable<string> allowedRoots)
{
    private readonly IReadOnlyList<string> _allowedRoots = allowedRoots.Select(PathUtils.Normalize).ToArray();

    public IReadOnlyList<string> AllowedRoots => _allowedRoots;

    public string ResolveFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A folder path is required.", nameof(path));
        }

        var fullPath = PathUtils.Normalize(path);

        if (!IsAllowed(fullPath))
        {
            throw new UnauthorizedAccessException(
                $"'{fullPath}' is outside the allowed paths ({string.Join(", ", _allowedRoots)}). " +
                "Add it to DOCSRAG_ALLOWED_PATHS to index it.");
        }

        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Folder not found: {fullPath}");
        }

        return fullPath;
    }

    public bool IsAllowed(string fullPath) =>
        _allowedRoots.Count == 0 || _allowedRoots.Any(root => PathUtils.IsSameOrUnder(fullPath, root));
}
