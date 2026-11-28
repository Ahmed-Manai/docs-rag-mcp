using System.IO.Compression;
using System.Xml;
using DocsRagMcp.Ingestion;

namespace DocsRagMcp.Tests;

public sealed class OdtReaderTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Odt_document_is_split_at_the_first_three_heading_levels()
    {
        var path = WriteOdt("report.odt", """
            <text:p>Introduction.</text:p>
            <text:h text:outline-level="1">Travel</text:h>
            <text:p>Book early.</text:p>
            <text:h text:outline-level="2">Hotels</text:h>
            <text:p>Stay in Malmö.</text:p>
            <text:h text:outline-level="3">Prices</text:h>
            <text:h text:outline-level="4">Details</text:h>
            <text:p>220 EUR.</text:p>
            """);

        Assert.Collection(DocumentReader.Read(path),
            part => Assert.Equal((null, "Introduction."), (part.Location, part.Text)),
            part => Assert.Equal(("Travel", "Travel\n\nBook early."), (part.Location, part.Text)),
            part => Assert.Equal(("Hotels", "Hotels\n\nStay in Malmö."), (part.Location, part.Text)),
            part => Assert.Equal(("Prices", "Prices\n\nDetails\n\n220 EUR."), (part.Location, part.Text)));
    }

    [Fact]
    public void Inline_formatting_and_explicit_whitespace_are_preserved()
    {
        var path = WriteOdt("inline.odt", """
            <text:p>Use <text:span>the guide</text:span><text:s text:c="2"/>today.<text:line-break/>Next<text:tab/>line.</text:p>
            <text:p><office:annotation><text:p>Hidden note</text:p></office:annotation>Visible.</text:p>
            """);

        var part = Assert.Single(DocumentReader.Read(path));
        Assert.Equal("Use the guide  today.\nNext\tline.\n\nVisible.", part.Text);
    }

    [Fact]
    public void Heading_only_sections_are_skipped()
    {
        var path = WriteOdt("headings.odt", """
            <text:h text:outline-level="1">Empty</text:h>
            <text:h text:outline-level="2">Content</text:h>
            <text:p>Details.</text:p>
            """);

        var part = Assert.Single(DocumentReader.Read(path));
        Assert.Equal(("Content", "Content\n\nDetails."), (part.Location, part.Text));
    }

    [Fact]
    public void Missing_content_is_rejected()
    {
        var path = _folder.Combine("missing.odt");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            zip.CreateEntry("other.xml");
        }

        Assert.Throws<InvalidDataException>(() => DocumentReader.Read(path));
    }

    [Fact]
    public void Document_type_definitions_are_not_processed()
    {
        var path = WriteOdt("dtd.odt", "<text:p>&a;</text:p>",
            "<!DOCTYPE office:document-content [<!ENTITY a 'unsafe'>]>");
        Assert.Throws<XmlException>(() => DocumentReader.Read(path));
    }

    [Theory]
    [InlineData("report.odt")]
    [InlineData("REPORT.ODT")]
    public void Odt_extensions_are_supported(string name) =>
        Assert.True(DocumentReader.IsSupported(name));

    private string WriteOdt(string name, string body, string prefix = "")
    {
        var path = _folder.Combine(name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("content.xml").Open());
        writer.Write($"""
            {prefix}<office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0">
              <office:body><office:text>{body}</office:text></office:body>
            </office:document-content>
            """);
        return path;
    }
}
