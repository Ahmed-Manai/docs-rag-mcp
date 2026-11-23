namespace DocsRagMcp;

/// <summary>Path helpers that behave the same way on Windows, macOS and Linux.</summary>
public static class PathUtils
{
    /// <summary>Linux file systems are case-sensitive; Windows and macOS (by default) are not.</summary>
    public static StringComparison Comparison { get; } =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public static StringComparer Comparer { get; } =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>Expands <c>~</c> and environment variables, and returns a full path without a trailing separator.</summary>
    public static string Normalize(string path)
    {
        path = path.Trim();

        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = path.Length > 2 ? Path.Combine(home, path[2..]) : home;
        }

        path = Environment.ExpandEnvironmentVariables(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="folder"/> itself or anything inside it.</summary>
    public static bool IsSameOrUnder(string path, string folder)
    {
        if (path.Equals(folder, Comparison))
        {
            return true;
        }

        var prefix = Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, Comparison);
    }
}
