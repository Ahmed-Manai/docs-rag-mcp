using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace DocsRagMcp.Ingestion;

/// <summary>A piece of a document, with a readable location such as a Markdown heading or a PDF page.</summary>
public sealed record DocumentPart(string Text, string? Location);

/// <summary>Turns Markdown, plain-text, HTML and PDF files into text parts that keep track of where they came from.</summary>
public static partial class DocumentReader
{
    private static readonly HashSet<string> MarkdownExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".mdx" };

    private static readonly HashSet<string> PlainTextExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".txt", ".rst", ".adoc" };

    private static readonly HashSet<string> HtmlExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".html", ".htm" };

    public static IReadOnlyCollection<string> SupportedExtensions { get; } =
        [.. MarkdownExtensions, .. PlainTextExtensions, .. HtmlExtensions, ".pdf"];

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<DocumentPart> Read(string path)
    {
        var extension = Path.GetExtension(path);

        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return ReadPdf(path);
        }

        if (HtmlExtensions.Contains(extension))
        {
            // Parsed from the stream, so the page's <meta charset> is respected.
            using var stream = File.OpenRead(path);
            return SplitHtml(new HtmlParser().ParseDocument(stream));
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

    /// <summary>
    /// Splits an HTML page at its h1 to h3 headings, like Markdown. When the page marks its content
    /// with a main element, only that is read. Scripts, styles, navigation and hidden elements are
    /// left out, and so are the page's own header and footer; a header or footer inside an article
    /// or main element is content and stays. The page title names the text before the first heading.
    /// </summary>
    public static IReadOnlyList<DocumentPart> SplitHtml(string html) => SplitHtml(new HtmlParser().ParseDocument(html));

    private static IReadOnlyList<DocumentPart> SplitHtml(IDocument document)
    {
        var parts = new List<DocumentPart>();
        var current = new StringBuilder();
        var title = Collapse(document.Title ?? string.Empty);
        string? heading = title.Length > 0 ? title : null;

        if ((document.QuerySelector("main") ?? document.QuerySelector("[role=main]") ?? document.Body) is { } content)
        {
            Walk(content, preformatted: false);
        }

        Flush();
        return parts;

        void Walk(INode node, bool preformatted)
        {
            foreach (var child in node.ChildNodes)
            {
                if (child is IText text)
                {
                    if (preformatted)
                    {
                        current.Append(text.Data);
                    }
                    else
                    {
                        // No space at the start of a line, so lines only start with whitespace in code blocks.
                        var collapsed = Collapse(text.Data, keepEdges: true);
                        current.Append(current.Length == 0 || current[^1] == '\n' ? collapsed.TrimStart() : collapsed);
                    }

                    continue;
                }

                if (child is not IElement element || IsSkipped(element))
                {
                    continue;
                }

                switch (element.LocalName)
                {
                    case "h1" or "h2" or "h3":
                        var headingText = Collapse(element.TextContent);
                        if (headingText.Length > 0)
                        {
                            Flush();
                            heading = headingText;
                            current.Append(headingText).Append("\n\n");
                        }

                        break;
                    case "br":
                        current.Append('\n');
                        break;
                    case "li":
                        StartLine();
                        current.Append("- ");
                        Walk(element, preformatted);
                        current.Append('\n');
                        break;
                    case "tr" or "dt" or "dd":
                        StartLine();
                        Walk(element, preformatted);
                        current.Append('\n');
                        break;
                    case "td" or "th":
                        current.Append(' ');
                        Walk(element, preformatted);
                        current.Append(' ');
                        break;
                    case "pre":
                        current.Append("\n\n");
                        Walk(element, preformatted: true);
                        current.Append("\n\n");
                        break;
                    default:
                        var isBlock = BlockElements.Contains(element.LocalName);
                        if (isBlock)
                        {
                            current.Append("\n\n");
                        }

                        Walk(element, preformatted);
                        if (isBlock)
                        {
                            current.Append("\n\n");
                        }

                        break;
                }
            }
        }

        void StartLine()
        {
            if (current.Length > 0 && current[^1] != '\n')
            {
                current.Append('\n');
            }
        }

        void Flush()
        {
            var text = CleanUp(current.ToString());
            current.Clear();

            // A heading directly followed by another heading carries no content of its own.
            if (text.Length > 0 && text != heading)
            {
                parts.Add(new DocumentPart(text, heading));
            }
        }
    }

    private static readonly HashSet<string> SkippedElements =
        new(StringComparer.OrdinalIgnoreCase) { "script", "style", "noscript", "template", "svg", "nav", "iframe", "button" };

    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "section", "article", "main", "aside", "header", "footer", "blockquote", "figure", "figcaption",
        "ul", "ol", "dl", "table", "h4", "h5", "h6", "hr", "address", "details", "summary",
    };

    private static readonly HashSet<string> SkippedRoles =
        new(StringComparer.OrdinalIgnoreCase) { "navigation", "banner", "contentinfo", "search" };

    private static bool IsSkipped(IElement element) =>
        SkippedElements.Contains(element.LocalName)
        || element.HasAttribute("hidden")
        || string.Equals(element.GetAttribute("aria-hidden"), "true", StringComparison.OrdinalIgnoreCase)
        || (element.GetAttribute("role") is { } role && SkippedRoles.Contains(role))
        || (element.LocalName is "header" or "footer" && element.Closest("article, main") is null);

    /// <summary>Collapses runs of whitespace to one space, like a browser does.</summary>
    private static string Collapse(string text, bool keepEdges = false)
    {
        var collapsed = Whitespace().Replace(text, " ");
        return keepEdges ? collapsed : collapsed.Trim();
    }

    /// <summary>Trims the end of each line and keeps at most one empty line between paragraphs.</summary>
    private static string CleanUp(string text)
    {
        var lines = text.Split('\n').Select(line => line.TrimEnd());
        return BlankLines().Replace(string.Join('\n', lines), "\n\n").Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();

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
