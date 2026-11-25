using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace DocsRagMcp.Ingestion;

/// <summary>A piece of a document, with a readable location such as a Markdown heading or a PDF page.</summary>
public sealed record DocumentPart(string Text, string? Location);

/// <summary>Turns Markdown, plain-text and PDF files into text parts that keep track of where they came from.</summary>
public static class DocumentReader
{
    private static readonly HashSet<string> MarkdownExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".mdx" };

    private static readonly HashSet<string> PlainTextExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".txt", ".rst", ".adoc" };

    public static IReadOnlyCollection<string> SupportedExtensions { get; } =
        [.. MarkdownExtensions, .. PlainTextExtensions, ".pdf"];

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<DocumentPart> Read(string path)
    {
        var extension = Path.GetExtension(path);

        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return ReadPdf(path);
        }

        var text = File.ReadAllText(path);
        return MarkdownExtensions.Contains(extension)
            ? SplitMarkdown(text)
            : string.IsNullOrWhiteSpace(text) ? [] : [new DocumentPart(text.Trim(), null)];
    }

    /// <summary>
    /// Splits Markdown at its headings, so each part knows which section it belongs to.
    /// Lines that look like headings inside code blocks are left alone. YAML front matter is left
    /// out; its title, if it has one, names the text before the first heading.
    /// </summary>
    public static IReadOnlyList<DocumentPart> SplitMarkdown(string markdown)
    {
        var parts = new List<DocumentPart>();
        var current = new StringBuilder();
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var start = FindFrontMatterEnd(lines, out var heading);
        var inCodeBlock = false;

        foreach (var line in lines.AsSpan(start))
        {
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                inCodeBlock = !inCodeBlock;
            }
            else if (!inCodeBlock && TryParseHeading(trimmed, out var title))
            {
                Flush();
                heading = title;
            }

            current.Append(line).Append('\n');
        }

        Flush();
        return parts;

        void Flush()
        {
            var text = current.ToString().Trim();
            current.Clear();

            // A heading directly followed by another heading carries no content of its own.
            var isHeadingOnly = !text.Contains('\n') && TryParseHeading(text, out _);
            if (text.Length > 0 && !isHeadingOnly)
            {
                parts.Add(new DocumentPart(text, heading));
            }
        }
    }

    /// <summary>
    /// Finds YAML front matter: a block that starts with a "---" line on the very first line of the
    /// file and ends with a "---" (or "...") line. Returns the number of lines it takes, 0 when
    /// there is none, and its title. A "---" line anywhere else is a horizontal rule and stays.
    /// </summary>
    private static int FindFrontMatterEnd(string[] lines, out string? title)
    {
        title = null;
        if (lines.Length == 0 || lines[0].TrimEnd() != "---")
        {
            return 0;
        }

        for (var end = 1; end < lines.Length; end++)
        {
            if (lines[end].TrimEnd() is not ("---" or "..."))
            {
                continue;
            }

            foreach (var line in lines.AsSpan(1, end - 1))
            {
                if (line.StartsWith("title:", StringComparison.Ordinal))
                {
                    var value = line["title:".Length..].Trim();
                    if (value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0])
                    {
                        value = value[1..^1].Trim();
                    }

                    // A block scalar ("title: |") spans several lines: not worth parsing YAML for.
                    title = value.Length > 0 && value[0] is not ('|' or '>') ? value : null;
                    break;
                }
            }

            return end + 1;
        }

        // No closing line: this is not front matter.
        return 0;
    }

    private static bool TryParseHeading(string line, out string title)
    {
        var level = 0;
        while (level < line.Length && line[level] == '#')
        {
            level++;
        }

        if (level is >= 1 and <= 6 && level < line.Length && line[level] == ' ')
        {
            title = line[(level + 1)..].Trim().TrimEnd('#').Trim();
            return title.Length > 0;
        }

        title = string.Empty;
        return false;
    }

    private static IReadOnlyList<DocumentPart> ReadPdf(string path)
    {
        using var document = PdfDocument.Open(path);
        var parts = new List<DocumentPart>();

        foreach (var page in document.GetPages())
        {
            var text = ContentOrderTextExtractor.GetText(page).Trim();
            if (text.Length > 0)
            {
                parts.Add(new DocumentPart(text, $"page {page.Number}"));
            }
        }

        return parts;
    }
}
