using DocsRagMcp.Indexing;

namespace DocsRagMcp.Tests;

public class PathGuardTests
{
    [Fact]
    public void Any_existing_folder_is_accepted_when_no_allowed_paths_are_set()
    {
        using var folder = new TempFolder();

        Assert.Equal(PathUtils.Normalize(folder.Path), new PathGuard([]).ResolveFolder(folder.Path));
    }

    [Fact]
    public void Subfolder_of_an_allowed_path_is_accepted()
    {
        using var folder = new TempFolder();
        var docs = Directory.CreateDirectory(folder.Combine("docs", "guides")).FullName;

        Assert.Equal(docs, new PathGuard([folder.Combine("docs")]).ResolveFolder(docs));
    }

    [Fact]
    public void Folder_outside_the_allowed_paths_is_rejected()
    {
        using var folder = new TempFolder();
        Directory.CreateDirectory(folder.Combine("docs"));
        var other = Directory.CreateDirectory(folder.Combine("other")).FullName;

        var error = Assert.Throws<UnauthorizedAccessException>(() => new PathGuard([folder.Combine("docs")]).ResolveFolder(other));
        Assert.Contains("DOCSRAG_ALLOWED_PATHS", error.Message);
    }

    [Fact]
    public void Sibling_folder_that_shares_a_name_prefix_is_rejected()
    {
        using var folder = new TempFolder();
        Directory.CreateDirectory(folder.Combine("docs"));
        var sibling = Directory.CreateDirectory(folder.Combine("docs-private")).FullName;

        Assert.Throws<UnauthorizedAccessException>(() => new PathGuard([folder.Combine("docs")]).ResolveFolder(sibling));
    }

    [Fact]
    public void Relative_path_cannot_climb_out_of_an_allowed_path()
    {
        using var folder = new TempFolder();
        Directory.CreateDirectory(folder.Combine("docs"));
        Directory.CreateDirectory(folder.Combine("secrets"));

        Assert.Throws<UnauthorizedAccessException>(
            () => new PathGuard([folder.Combine("docs")]).ResolveFolder(folder.Combine("docs", "..", "secrets")));
    }

    [Fact]
    public void Missing_folder_is_reported()
    {
        using var folder = new TempFolder();

        Assert.Throws<DirectoryNotFoundException>(() => new PathGuard([]).ResolveFolder(folder.Combine("missing")));
    }
}
