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
    /// Lines that look like headings inside code blocks are left alone.
    /// </summary>
    public static IReadOnlyList<DocumentPart> SplitMarkdown(string markdown)
    {
        var parts = new List<DocumentPart>();
        var current = new StringBuilder();
        string? heading = null;
        var inCodeBlock = false;

        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
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
