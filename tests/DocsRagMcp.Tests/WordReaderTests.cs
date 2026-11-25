using System.IO.Compression;
using System.Xml;
using DocsRagMcp.Ingestion;

namespace DocsRagMcp.Tests;

public sealed class WordReaderTests : IDisposable
{
    private const string Namespaces =
        "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
        "xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"";

    private const string EnglishStyles = """
        <w:style w:type="paragraph" w:styleId="Normal"><w:name w:val="Normal"/></w:style>
        <w:style w:type="paragraph" w:styleId="Title"><w:name w:val="Title"/></w:style>
        <w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/></w:style>
        <w:style w:type="paragraph" w:styleId="Heading2"><w:name w:val="heading 2"/></w:style>
        <w:style w:type="paragraph" w:styleId="Heading4"><w:name w:val="heading 4"/></w:style>
        <w:style w:type="paragraph" w:styleId="MyHeading"><w:name w:val="My heading"/><w:basedOn w:val="Heading2"/></w:style>
        <w:style w:type="paragraph" w:styleId="ListBullet"><w:name w:val="List Bullet"/><w:pPr><w:numPr><w:numId w:val="2"/></w:numPr></w:pPr></w:style>
        <w:style w:type="paragraph" w:styleId="MyBullet"><w:name w:val="My bullet"/><w:basedOn w:val="ListBullet"/></w:style>
        <w:style w:type="character" w:styleId="Heading1Char"><w:name w:val="Heading 1 Char"/></w:style>
        """;

    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void Word_document_is_split_at_its_title_and_headings()
    {
        var path = WriteDocx("policy.docx", EnglishStyles, title: null, body: string.Concat(
            P("Travel policy", style: "Title"),
            P("Book trips early."),
            P("Hotels", style: "Heading1"),
            "<w:p><w:r><w:t xml:space=\"preserve\">Use the </w:t></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>travel</w:t></w:r><w:r><w:t xml:space=\"preserve\"> portal.</w:t></w:r></w:p>",
            P("Up to 220 EUR in Paris", list: true),
            P("Up to 150 EUR elsewhere", style: "ListBullet"),
            P("Breakfast included", style: "MyBullet"),
            P("Ask your manager."),
            P("Details", style: "Heading4"),
            "<w:tbl><w:tr><w:tc>" + P("City") + "</w:tc><w:tc>" + P("Limit") + "</w:tc></w:tr>" +
            "<w:tr><w:tc>" + P("Paris") + "</w:tc><w:tc>" + P("220 EUR") + "</w:tc></w:tr></w:tbl>",
            P("Taxis", style: "MyHeading"),
            "<w:p><w:pPr><w:tabs><w:tab w:val=\"left\" w:pos=\"720\"/></w:tabs></w:pPr><w:r><w:t>Line one</w:t><w:br/><w:t>Line</w:t><w:tab/><w:t>two</w:t></w:r></w:p>",
            P("   ")));

        Assert.Collection(
            DocumentReader.Read(path),
            part =>
            {
                Assert.Equal("Travel policy", part.Location);
                Assert.Equal("Travel policy\n\nBook trips early.", part.Text);
            },
            part =>
            {
                Assert.Equal("Hotels", part.Location);
                Assert.Equal(
                    "Hotels\n\nUse the travel portal.\n\n- Up to 220 EUR in Paris\n- Up to 150 EUR elsewhere\n- Breakfast included\n\nAsk your manager.\n\nDetails\n\nCity | Limit\nParis | 220 EUR",
                    part.Text);
            },
            part =>
            {
                Assert.Equal("Taxis", part.Location);
                Assert.Equal("Taxis\n\nLine one\nLine\ttwo", part.Text);
            });
    }

    [Fact]
    public void Translated_style_ids_outline_levels_and_the_document_title_are_used()
    {
        const string germanStyles = """
            <w:style w:type="paragraph" w:styleId="berschrift1"><w:name w:val="heading 1"/></w:style>
            <w:style w:type="paragraph" w:styleId="Gliederung"><w:name w:val="Gliederung"/><w:pPr><w:outlineLvl w:val="1"/></w:pPr></w:style>
            """;

        var path = WriteDocx("bericht.docx", germanStyles, title: "Reisebericht", body: string.Concat(
            P("Einleitender Text."),
            P("Einleitung", style: "berschrift1"),
            P("Erster Abschnitt."),
            P("Kosten", style: "Gliederung"),
            P("Zweiter Abschnitt."),
            "<w:p><w:pPr><w:outlineLvl w:val=\"2\"/></w:pPr><w:r><w:t>Direkt</w:t></w:r></w:p>",
            P("Dritter Abschnitt."),
            "<w:p><w:pPr><w:outlineLvl w:val=\"9\"/></w:pPr><w:r><w:t>Body text level.</w:t></w:r></w:p>"));

        Assert.Collection(
            DocumentReader.Read(path),
            part => Assert.Equal(("Reisebericht", "Einleitender Text."), (part.Location, part.Text)),
            part => Assert.Equal(("Einleitung", "Einleitung\n\nErster Abschnitt."), (part.Location, part.Text)),
            part => Assert.Equal(("Kosten", "Kosten\n\nZweiter Abschnitt."), (part.Location, part.Text)),
            part => Assert.Equal(("Direkt", "Direkt\n\nDritter Abschnitt.\n\nBody text level."), (part.Location, part.Text)));
    }

    [Fact]
    public void Deleted_text_field_codes_and_fallback_content_are_left_out()
    {
        var path = WriteDocx("review.docx", EnglishStyles, title: null, body:
            "<w:p>" +
            "<w:r><w:t xml:space=\"preserve\">See </w:t></w:r>" +
            "<w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText> HYPERLINK \"https://example.com\" </w:instrText></w:r>" +
            "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>the guide</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r>" +
            "<w:del w:id=\"1\" w:author=\"A\"><w:r><w:delText> (old link)</w:delText></w:r></w:del>" +
            "<w:ins w:id=\"2\" w:author=\"A\"><w:r><w:t xml:space=\"preserve\"> today</w:t></w:r></w:ins>" +
            "<mc:AlternateContent><mc:Choice Requires=\"wps\"><w:r><w:t xml:space=\"preserve\"> [box]</w:t></w:r></mc:Choice>" +
            "<mc:Fallback><w:r><w:t xml:space=\"preserve\"> [box]</w:t></w:r></mc:Fallback></mc:AlternateContent>" +
            "<w:hyperlink><w:r><w:t>.</w:t></w:r></w:hyperlink>" +
            "</w:p>");

        var part = Assert.Single(DocumentReader.Read(path));

        Assert.Equal("See the guide today [box].", part.Text);
    }

    [Fact]
    public void English_heading_style_ids_work_without_styles_xml()
    {
        var path = WriteDocx("plain.docx", styles: null, title: null, body: P("Setup", style: "Heading1") + P("Install the app."));

        var part = Assert.Single(DocumentReader.Read(path));

        Assert.Equal(("Setup", "Setup\n\nInstall the app."), (part.Location, part.Text));
    }

    [Fact]
    public void Content_controls_are_read()
    {
        var path = WriteDocx("form.docx", EnglishStyles, title: null, body:
            "<w:sdt><w:sdtPr/><w:sdtContent>" + P("Contents", style: "Heading1") + P("Inside a content control.") + "</w:sdtContent></w:sdt>");

        var part = Assert.Single(DocumentReader.Read(path));

        Assert.Equal(("Contents", "Contents\n\nInside a content control."), (part.Location, part.Text));
    }

    [Fact]
    public void A_file_that_is_not_a_Word_document_is_rejected()
    {
        var notZip = _folder.Write("notes.docx", "just text");
        Assert.Throws<InvalidDataException>(() => DocumentReader.Read(notZip));

        var otherZip = _folder.Combine("other.docx");
        using (var zip = ZipFile.Open(otherZip, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("hello.txt").Open());
            writer.Write("hello");
        }

        var error = Assert.Throws<InvalidDataException>(() => DocumentReader.Read(otherZip));
        Assert.Contains("word/document.xml", error.Message);
    }

    [Fact]
    public void Document_type_definitions_are_not_processed()
    {
        var path = _folder.Combine("dtd.docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("word/document.xml").Open());
            writer.Write($"<?xml version=\"1.0\"?><!DOCTYPE w:document [<!ENTITY a \"aaaaaaaaaa\">]><w:document {Namespaces}><w:body>{P("&a;")}</w:body></w:document>");
        }

        Assert.Throws<XmlException>(() => DocumentReader.Read(path));
    }

    [Theory]
    [InlineData("report.docx", true)]
    [InlineData("REPORT.DOCX", true)]
    [InlineData("~$report.docx", false)]
    [InlineData("report.doc", false)]
    public void Word_files_are_supported_but_not_their_lock_files(string fileName, bool supported) =>
        Assert.Equal(supported, DocumentReader.IsSupported(Path.Combine(_folder.Path, fileName)));

    private static string P(string text, string? style = null, bool list = false)
    {
        var properties = (style is null ? "" : $"<w:pStyle w:val=\"{style}\"/>") +
            (list ? "<w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr>" : "");
        return $"<w:p><w:pPr>{properties}</w:pPr><w:r><w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>";
    }

    private string WriteDocx(string name, string? styles, string? title, string body)
    {
        var path = _folder.Combine(name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "word/document.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document {Namespaces}><w:body>{body}</w:body></w:document>");
        if (styles is not null)
        {
            Add(zip, "word/styles.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:styles {Namespaces}>{styles}</w:styles>");
        }

        if (title is not null)
        {
            Add(zip, "docProps/core.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><cp:coreProperties " +
                "xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" " +
                $"xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>{title}</dc:title></cp:coreProperties>");
        }

        return path;
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(content);
    }
}
