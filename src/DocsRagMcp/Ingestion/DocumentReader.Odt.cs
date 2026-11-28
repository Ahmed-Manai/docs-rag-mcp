using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace DocsRagMcp.Ingestion;

public static partial class DocumentReader
{
    private static readonly XNamespace OdfOffice = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace OdfText = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";

    /// <summary>Reads OpenDocument Text (.odt), splitting at outline levels 1 to 3.</summary>
    public static IReadOnlyList<DocumentPart> ReadOdt(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var document = LoadPart(zip, "content.xml")
            ?? throw new InvalidDataException("The file has no content.xml part, so it is not an OpenDocument Text document.");
        var body = document.Root?.Element(OdfOffice + "body")?.Element(OdfOffice + "text")
            ?? throw new InvalidDataException("The file has no OpenDocument Text body.");

        var parts = new List<DocumentPart>();
        var current = new StringBuilder();
        string? heading = null;
        var hasContent = false;

        foreach (var paragraph in body.Descendants().Where(element =>
                     element.Name == OdfText + "p" || element.Name == OdfText + "h"))
        {
            // Do not read nested paragraphs twice (for example, in an annotation).
            if (paragraph.Ancestors().Any(element =>
                    element.Name == OdfText + "p" || element.Name == OdfText + "h"))
            {
                continue;
            }

            var text = OdtParagraphText(paragraph).Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (paragraph.Name == OdfText + "h"
                && int.TryParse(paragraph.Attribute(OdfText + "outline-level")?.Value, out var level)
                && level is >= 1 and <= 3)
            {
                Flush();
                heading = text;
            }
            else
            {
                hasContent = true;
            }

            current.Append(text).Append("\n\n");
        }

        Flush();
        return parts;

        void Flush()
        {
            var text = CleanUp(current.ToString());
            current.Clear();

            if (hasContent && text.Length > 0)
            {
                parts.Add(new DocumentPart(text, heading));
            }

            hasContent = false;
        }
    }

    private static string OdtParagraphText(XElement paragraph)
    {
        var text = new StringBuilder();
        foreach (var node in paragraph.DescendantNodes())
        {
            if (node.Ancestors().Any(element => element.Name == OdfOffice + "annotation"
                || element.Name == OdfText + "note" || element.Name == OdfText + "tracked-changes"))
            {
                continue;
            }

            if (node is XText value)
            {
                text.Append(value.Value);
            }
            else if (node is XElement element)
            {
                if (element.Name == OdfText + "s")
                {
                    // A run of explicit spaces; cap expansion of untrusted attributes.
                    var count = int.TryParse(element.Attribute(OdfText + "c")?.Value, out var spaces)
                        ? Math.Clamp(spaces, 1, 1024) : 1;
                    text.Append(' ', count);
                }
                else if (element.Name == OdfText + "tab")
                {
                    text.Append('\t');
                }
                else if (element.Name == OdfText + "line-break")
                {
                    text.Append('\n');
                }
            }
        }

        return text.ToString();
    }
}
