using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DocsRagMcp.Ingestion;

public static partial class DocumentReader
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";

    // A .docx file is small, but the XML inside it is compressed: refuse parts that expand to more than this.
    private const long MaxWordPartBytes = 64 * 1024 * 1024;

    private const int TitleLevel = 0;
    private const int NotAHeading = int.MaxValue;

    /// <summary>
    /// Reads a Word (.docx) document, which is a zip of XML parts, and splits it at its Title and
    /// Heading 1 to 3 paragraphs, like Markdown. The document title from its properties names the
    /// text before the first heading. List items become "- " lines and table rows "cell | cell" lines.
    /// </summary>
    public static IReadOnlyList<DocumentPart> ReadWord(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var document = LoadPart(zip, "word/document.xml")
            ?? throw new InvalidDataException("The file has no word/document.xml part, so it is not a Word document.");
        var (headingStyles, listStyles) = ReadStyles(LoadPart(zip, "word/styles.xml"));
        var title = LoadPart(zip, "docProps/core.xml")?.Descendants(Dc + "title").FirstOrDefault()?.Value.Trim();

        var parts = new List<DocumentPart>();
        var current = new StringBuilder();
        string? heading = string.IsNullOrEmpty(title) ? null : title;
        var inList = false;

        if (document.Root?.Element(W + "body") is { } body)
        {
            ReadBlocks(body);
        }

        Flush();
        return parts;

        void ReadBlocks(XElement container)
        {
            foreach (var element in container.Elements())
            {
                if (element.Name == W + "p")
                {
                    ReadParagraph(element);
                }
                else if (element.Name == W + "tbl")
                {
                    ReadTable(element);
                }
                else if (element.Name == W + "sdt" && element.Element(W + "sdtContent") is { } content)
                {
                    // A content control, such as a table of contents or a form field.
                    ReadBlocks(content);
                }
            }
        }

        void ReadParagraph(XElement paragraph)
        {
            var text = ParagraphText(paragraph).Trim();
            if (text.Length == 0)
            {
                return;
            }

            if (HeadingLevel(paragraph, headingStyles) <= 3)
            {
                Flush();
                heading = text;
                current.Append(text).Append("\n\n");
                inList = false;
                return;
            }

            // Numbering is set on the paragraph, or on its style, as in Word's "List Bullet".
            var properties = paragraph.Element(W + "pPr");
            if (properties?.Element(W + "numPr") is not null
                || (properties?.Element(W + "pStyle")?.Attribute(W + "val")?.Value is { } styleId && listStyles.Contains(styleId)))
            {
                current.Append("- ").Append(text).Append('\n');
                inList = true;
                return;
            }

            EndList();
            current.Append(text).Append("\n\n");
        }

        void ReadTable(XElement table)
        {
            EndList();
            foreach (var row in table.Elements(W + "tr"))
            {
                var cells = row.Elements(W + "tc")
                    .Select(cell => string.Join(" ", cell.Descendants(W + "p").Select(p => ParagraphText(p).Trim()).Where(t => t.Length > 0)))
                    .ToList();
                if (cells.Any(cell => cell.Length > 0))
                {
                    current.Append(string.Join(" | ", cells)).Append('\n');
                }
            }

            current.Append('\n');
        }

        void EndList()
        {
            if (inList)
            {
                current.Append('\n');
                inList = false;
            }
        }

        void Flush()
        {
            var text = CleanUp(current.ToString());
            current.Clear();
            inList = false;

            // A heading directly followed by another heading carries no content of its own.
            if (text.Length > 0 && text != heading)
            {
                parts.Add(new DocumentPart(text, heading));
            }
        }
    }

    /// <summary>The text of a paragraph's runs, without deleted text, field codes or duplicate fallback content.</summary>
    private static string ParagraphText(XElement paragraph)
    {
        var text = new StringBuilder();
        Collect(paragraph);
        return text.ToString();

        void Collect(XElement element)
        {
            foreach (var child in element.Elements())
            {
                var name = child.Name;
                if (name == Mc + "Fallback" || name == W + "pPr" || name == W + "rPr")
                {
                    // Fallback repeats the content of the preferred choice for older readers;
                    // pPr and rPr hold formatting, including tab stop definitions.
                    continue;
                }

                if (name == W + "t")
                {
                    text.Append(child.Value);
                }
                else if (name == W + "tab")
                {
                    text.Append('\t');
                }
                else if (name == W + "br" || name == W + "cr")
                {
                    text.Append('\n');
                }
                else if (name == W + "noBreakHyphen")
                {
                    text.Append('-');
                }
                else
                {
                    Collect(child);
                }
            }
        }
    }

    /// <summary>0 for a title, 1 to 9 for headings, <see cref="NotAHeading"/> for other paragraphs.</summary>
    private static int HeadingLevel(XElement paragraph, IReadOnlyDictionary<string, int> headingStyles)
    {
        var properties = paragraph.Element(W + "pPr");
        if (OutlineLevel(properties) is { } outline)
        {
            return outline;
        }

        var styleId = properties?.Element(W + "pStyle")?.Attribute(W + "val")?.Value;
        if (styleId is null)
        {
            return NotAHeading;
        }

        if (headingStyles.TryGetValue(styleId, out var level))
        {
            return level;
        }

        // Without styles.xml, fall back to the style ids Word uses in English.
        return styleId.Equals("Title", StringComparison.OrdinalIgnoreCase) ? TitleLevel
            : HeadingStyleId().Match(styleId) is { Success: true } match ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : NotAHeading;
    }

    /// <summary>
    /// Maps paragraph style ids to heading levels, and finds the list styles. Style ids are translated
    /// in non-English versions of Word ("berschrift1", "Titre1"), but the names of the built-in styles
    /// are always "heading 1" and "Title". A style based on a heading or list style is one too.
    /// </summary>
    private static (Dictionary<string, int> HeadingLevels, HashSet<string> ListStyles) ReadStyles(XDocument? styles)
    {
        var directLevels = new Dictionary<string, int>(StringComparer.Ordinal);
        var directLists = new HashSet<string>(StringComparer.Ordinal);
        var basedOn = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var style in styles?.Root?.Elements(W + "style") ?? [])
        {
            if (style.Attribute(W + "type")?.Value != "paragraph" || style.Attribute(W + "styleId")?.Value is not { } id)
            {
                continue;
            }

            var name = style.Element(W + "name")?.Attribute(W + "val")?.Value ?? string.Empty;
            var properties = style.Element(W + "pPr");
            if (name.Equals("Title", StringComparison.OrdinalIgnoreCase))
            {
                directLevels[id] = TitleLevel;
            }
            else if (HeadingStyleName().Match(name) is { Success: true } match)
            {
                directLevels[id] = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (OutlineLevel(properties) is { } outline)
            {
                directLevels[id] = outline;
            }

            if (properties?.Element(W + "numPr") is not null)
            {
                directLists.Add(id);
            }

            if (style.Element(W + "basedOn")?.Attribute(W + "val")?.Value is { } parent)
            {
                basedOn[id] = parent;
            }
        }

        var levels = new Dictionary<string, int>(directLevels, StringComparer.Ordinal);
        var lists = new HashSet<string>(directLists, StringComparer.Ordinal);
        foreach (var id in basedOn.Keys)
        {
            // Walk up the chain of base styles, with a limit in case a document has a cycle.
            var ancestor = id;
            var foundLevel = directLevels.ContainsKey(id);
            var foundList = directLists.Contains(id);
            for (var depth = 0; depth < 10 && !(foundLevel && foundList) && basedOn.TryGetValue(ancestor, out var parent); depth++)
            {
                if (!foundLevel && directLevels.TryGetValue(parent, out var level))
                {
                    levels[id] = level;
                    foundLevel = true;
                }

                if (!foundList && directLists.Contains(parent))
                {
                    lists.Add(id);
                    foundList = true;
                }

                ancestor = parent;
            }
        }

        return (levels, lists);
    }

    /// <summary>Word stores outline levels from 0 (heading 1) to 8, and 9 for body text.</summary>
    private static int? OutlineLevel(XElement? properties) =>
        int.TryParse(properties?.Element(W + "outlineLvl")?.Attribute(W + "val")?.Value, out var level) && level is >= 0 and <= 8
            ? level + 1
            : null;

    private static XDocument? LoadPart(ZipArchive zip, string name)
    {
        if (zip.GetEntry(name) is not { } entry)
        {
            return null;
        }

        if (entry.Length > MaxWordPartBytes)
        {
            throw new InvalidDataException($"{name} is too large ({entry.Length} bytes once decompressed).");
        }

        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader);
    }

    [GeneratedRegex(@"^heading ([1-9])$", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingStyleName();

    [GeneratedRegex(@"^Heading([1-9])$", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingStyleId();
}
