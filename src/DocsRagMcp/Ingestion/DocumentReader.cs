using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace DocsRagMcp.Ingestion;

/// <summary>A piece of a document, with a readable location such as a Markdown heading or a PDF page.</summary>
public sealed record DocumentPart(string Text, string? Location);

/// <summary>Turns Markdown, plain-text, HTML, Word and PDF files into text parts that keep track of where they came from.</summary>
public static partial class DocumentReader
{
    private static readonly HashSet<string> MarkdownExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".mdx" };

    private static readonly HashSet<string> PlainTextExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".txt" };

    private static readonly HashSet<string> RestructuredTextExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".rst" };

    private static readonly HashSet<string> AsciiDocExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".adoc" };

    private static readonly HashSet<string> HtmlExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".html", ".htm" };

    public static IReadOnlyCollection<string> SupportedExtensions { get; } =
        [.. MarkdownExtensions, .. PlainTextExtensions, .. RestructuredTextExtensions, .. AsciiDocExtensions, .. HtmlExtensions, ".docx", ".pdf"];

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
        // Word keeps a "~$name.docx" lock file next to an open document. It is hidden on Windows,
        // but not on macOS or Linux, and it is not a document.
        && !Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal);

    public static IReadOnlyList<DocumentPart> Read(string path)
    {
        var extension = Path.GetExtension(path);

        if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return ReadPdf(path);
        }

        if (extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(path);
            return ReadWord(stream);
        }

        if (HtmlExtensions.Contains(extension))
        {
            // Parsed from the stream, so the page's <meta charset> is respected.
            using var stream = File.OpenRead(path);
            return SplitHtml(new HtmlParser().ParseDocument(stream));
        }

        var text = File.ReadAllText(path);
        if (RestructuredTextExtensions.Contains(extension))
        {
            return SplitRestructuredText(text);
        }

        if (AsciiDocExtensions.Contains(extension))
        {
            return SplitAsciiDoc(text);
        }

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
    /// Splits AsciiDoc at its headings, the way Markdown files are split. Only the first three
    /// heading levels (<c>=</c>, <c>==</c>, <c>===</c>) split; deeper ones stay as text. Lines
    /// inside <c>----</c> (listing) and <c>....</c> (literal) blocks are not headings.
    /// </summary>
    public static IReadOnlyList<DocumentPart> SplitAsciiDoc(string asciidoc)
    {
        var parts = new List<DocumentPart>();
        var current = new StringBuilder();
        var lines = asciidoc.ReplaceLineEndings("\n").Split('\n');
        string? heading = null;
        var hasBody = false;
        var inLiteralBlock = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();

            if (trimmed is "----" or "....")
            {
                inLiteralBlock = !inLiteralBlock;
            }
            else if (!inLiteralBlock && TryParseAsciiDocHeading(line, out var title))
            {
                Flush();
                heading = title;
                hasBody = false;
            }
            else if (line.Trim().Length > 0)
            {
                // Blank lines carry no content: a heading followed only by blank lines
                // and another heading is skipped, the way Markdown does it.
                hasBody = true;
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
            if (text.Length > 0 && hasBody)
            {
                parts.Add(new DocumentPart(text, heading));
            }
        }
    }

    private static bool TryParseAsciiDocHeading(string line, out string title)
    {
        var level = 0;
        while (level < line.Length && line[level] == '=')
        {
            level++;
        }

        if (level is >= 1 and <= 3 && level < line.Length && line[level] == ' ')
        {
            title = line[(level + 1)..].Trim();
            return title.Length > 0;
        }

        title = string.Empty;
        return false;
    }

    /// <summary>
    /// Splits reStructuredText at its headings, the way Markdown files are split. A heading is a
    /// line of text underlined (and sometimes overlined) with a row of the same punctuation
    /// character, at least as long as the text. The level comes from the order in which the
    /// underline styles first appear in the file; only the first three levels split.
    /// </summary>
    public static IReadOnlyList<DocumentPart> SplitRestructuredText(string rst)
    {
        var parts = new List<DocumentPart>();
        var current = new StringBuilder();
        var lines = rst.ReplaceLineEndings("\n").Split('\n');
        string? heading = null;
        var hasBody = false;
        var styles = new List<char>();

        var i = 0;
        while (i < lines.Length)
        {
            // An overlined heading takes three lines; the underline-only form takes two. Check
            // the overlined form first so its overline is not left behind as body text.
            if (i + 2 < lines.Length
                && TryParseRstHeading(lines[i + 1], lines[i], lines[i + 2], styles, out var overTitle, out var overLevel))
            {
                if (overLevel <= 3)
                {
                    Flush();
                    heading = overTitle;
                    hasBody = false;
                }
                else
                {
                    hasBody = true;
                }

                current.Append(lines[i]).Append('\n');
                current.Append(lines[i + 1]).Append('\n');
                current.Append(lines[i + 2]).Append('\n');
                i += 3;
                continue;
            }

            if (i + 1 < lines.Length
                && TryParseRstHeading(lines[i], null, lines[i + 1], styles, out var title, out var level))
            {
                if (level <= 3)
                {
                    Flush();
                    heading = title;
                    hasBody = false;
                }
                else
                {
                    hasBody = true;
                }

                current.Append(lines[i]).Append('\n');
                current.Append(lines[i + 1]).Append('\n');
                i += 2;
                continue;
            }

            if (lines[i].Trim().Length > 0)
            {
                // Blank lines carry no content: a heading followed only by blank lines
                // and another heading is skipped, the way Markdown does it.
                hasBody = true;
            }

            current.Append(lines[i]).Append('\n');
            i++;
        }

        Flush();
        return parts;

        void Flush()
        {
            var text = current.ToString().Trim();
            current.Clear();

            // A heading directly followed by another heading carries no content of its own.
            if (text.Length > 0 && hasBody)
            {
                parts.Add(new DocumentPart(text, heading));
            }
        }
    }

    private static bool TryParseRstHeading(string textLine, string? overline, string underline, List<char> styles, out string title, out int level)
    {
        title = string.Empty;
        level = 0;
        var text = textLine.Trim();
        if (text.Length == 0 || !text.Any(char.IsLetterOrDigit))
        {
            return false;
        }

        var adornment = underline.TrimEnd();
        if (adornment.Length < text.Length)
        {
            return false;
        }

        var mark = adornment[0];
        if (char.IsLetterOrDigit(mark) || char.IsWhiteSpace(mark) || adornment.Any(c => c != mark))
        {
            return false;
        }

        if (overline is not null)
        {
            var over = overline.TrimEnd();
            if (over.Length < text.Length || over.Any(c => c != mark))
            {
                return false;
            }
        }

        var index = styles.IndexOf(mark);
        if (index < 0)
        {
            styles.Add(mark);
            index = styles.Count - 1;
        }

        level = index + 1;
        title = text;
        return true;
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
