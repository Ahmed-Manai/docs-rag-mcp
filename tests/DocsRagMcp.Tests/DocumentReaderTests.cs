using DocsRagMcp.Ingestion;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace DocsRagMcp.Tests;

public class DocumentReaderTests
{
    [Fact]
    public void Markdown_is_split_at_headings()
    {
        var parts = DocumentReader.SplitMarkdown("Intro text.\n\n# Install\nRun the installer.\n\n## Windows\nUse winget.\n");

        Assert.Collection(
            parts,
            part =>
            {
                Assert.Null(part.Location);
                Assert.Equal("Intro text.", part.Text);
            },
            part =>
            {
                Assert.Equal("Install", part.Location);
                Assert.Contains("Run the installer.", part.Text);
            },
            part =>
            {
                Assert.Equal("Windows", part.Location);
                Assert.Contains("Use winget.", part.Text);
            });
    }

    [Fact]
    public void Hash_lines_inside_code_blocks_are_not_headings()
    {
        var part = Assert.Single(DocumentReader.SplitMarkdown("# Setup\n```bash\n# install dependencies\nnpm install\n```\nDone."));

        Assert.Equal("Setup", part.Location);
        Assert.Contains("# install dependencies", part.Text);
    }

    [Fact]
    public void Heading_with_no_text_of_its_own_is_skipped()
    {
        var part = Assert.Single(DocumentReader.SplitMarkdown("# Guide\n## Part one\nSome text."));

        Assert.Equal("Part one", part.Location);
    }

    [Theory]
    [InlineData("title: Travel policy")]
    [InlineData("title: \"Travel policy\"")]
    [InlineData("title: 'Travel policy'")]
    public void Front_matter_is_left_out_and_its_title_names_the_text_before_the_first_heading(string titleLine)
    {
        var parts = DocumentReader.SplitMarkdown(
            $"---\n{titleLine}\ntags: [hr, travel]\n---\n\nBook trips early.\n\n# Hotels\nUse the travel portal.\n");

        Assert.Collection(
            parts,
            part =>
            {
                Assert.Equal("Travel policy", part.Location);
                Assert.Equal("Book trips early.", part.Text);
            },
            part =>
            {
                Assert.Equal("Hotels", part.Location);
                Assert.Equal("# Hotels\nUse the travel portal.", part.Text);
            });
    }

    [Fact]
    public void Front_matter_without_a_title_is_left_out()
    {
        var part = Assert.Single(DocumentReader.SplitMarkdown("---\r\nlayout: post\r\ndate: 2026-01-05\r\n...\r\n# Release notes\r\nFaster search."));

        Assert.Equal("Release notes", part.Location);
        Assert.Equal("# Release notes\nFaster search.", part.Text);
    }

    [Fact]
    public void Horizontal_rule_later_in_the_file_is_kept()
    {
        var part = Assert.Single(DocumentReader.SplitMarkdown("Intro.\n\n---\n\ntitle: not front matter\n\n---\n\nMore text."));

        Assert.Null(part.Location);
        Assert.Equal("Intro.\n\n---\n\ntitle: not front matter\n\n---\n\nMore text.", part.Text);
    }

    [Fact]
    public void Opening_rule_without_a_closing_one_is_kept()
    {
        var part = Assert.Single(DocumentReader.SplitMarkdown("---\ntitle: Draft\nStill writing this."));

        Assert.Null(part.Location);
        Assert.Contains("title: Draft", part.Text);
    }

    [Fact]
    public void Multi_line_title_is_ignored()
    {
        var part = Assert.Single(DocumentReader.SplitMarkdown("---\ntitle: |\n  Long title\n---\nBody."));

        Assert.Null(part.Location);
        Assert.Equal("Body.", part.Text);
    }

    [Fact]
    public void Html_is_split_at_headings_without_scripts_styles_and_navigation()
    {
        var parts = DocumentReader.SplitHtml("""
            <!doctype html>
            <html>
            <head>
              <title>Travel policy</title>
              <style>.menu { color: red; }</style>
              <script>var tracking = "secret";</script>
            </head>
            <body>
              <header><a href="/">Company intranet</a></header>
              <nav><a href="/">Home</a> | <a href="/hr">HR</a></nav>
              <p>Book   trips
                 early.</p>
              <h2>Hotels</h2>
              <p>Use the <b>travel</b> portal&nbsp;only.</p>
              <ul><li>Up to 220 EUR in Paris</li><li>Up to 150 EUR elsewhere</li></ul>
              <h4>Exceptions</h4>
              <p>Ask your manager.</p>
              <footer>© 2026 Company</footer>
            </body>
            </html>
            """);

        Assert.Collection(
            parts,
            part =>
            {
                Assert.Equal("Travel policy", part.Location);
                Assert.Equal("Book trips early.", part.Text);
            },
            part =>
            {
                Assert.Equal("Hotels", part.Location);
                Assert.Equal(
                    "Hotels\n\nUse the travel portal only.\n\n- Up to 220 EUR in Paris\n- Up to 150 EUR elsewhere\n\nExceptions\n\nAsk your manager.",
                    part.Text);
            });

        var all = string.Join("\n", parts.Select(p => p.Text));
        Assert.DoesNotContain("tracking", all);
        Assert.DoesNotContain("color", all);
        Assert.DoesNotContain("intranet", all);
        Assert.DoesNotContain("Home", all);
        Assert.DoesNotContain("2026", all);
    }

    [Fact]
    public void Html_article_header_is_kept_because_it_holds_the_title()
    {
        var part = Assert.Single(DocumentReader.SplitHtml(
            "<body><header>Site name</header><main><article><header><h1>Release notes</h1></header><p>Faster search.</p></article></main></body>"));

        Assert.Equal("Release notes", part.Location);
        Assert.Equal("Release notes\n\nFaster search.", part.Text);
    }

    [Fact]
    public void Html_code_blocks_keep_their_line_breaks()
    {
        var part = Assert.Single(DocumentReader.SplitHtml(
            "<h1>Install</h1><pre><code>dotnet tool install\n  --global DocsRagMcp</code></pre><p>Then restart.</p>"));

        Assert.Equal("Install\n\ndotnet tool install\n  --global DocsRagMcp\n\nThen restart.", part.Text);
    }

    [Fact]
    public void Html_main_element_is_read_without_the_rest_of_the_page()
    {
        var part = Assert.Single(DocumentReader.SplitHtml("""
            <body>
              <a href="#main">Skip to main content</a>
              <div class="sidebar"><a href="/a">Getting started</a><a href="/b">Reference</a></div>
              <main id="main">
                <h1>Tools</h1>
                <div role="search">Search the docs</div>
                <p aria-hidden="true">Decorative text</p>
                <p>Servers expose tools.</p>
              </main>
              <div role="contentinfo">Privacy</div>
            </body>
            """));

        Assert.Equal("Tools", part.Location);
        Assert.Equal("Tools\n\nServers expose tools.", part.Text);
    }

    [Fact]
    public void Html_without_text_has_no_parts()
    {
        Assert.Empty(DocumentReader.SplitHtml("<html><head><title>Empty</title></head><body><script>run()</script></body></html>"));
    }

    [Theory]
    [InlineData("guide.html")]
    [InlineData("guide.HTM")]
    public void Html_files_are_read(string fileName)
    {
        using var folder = new TempFolder();
        var path = folder.Write(fileName, "<html><head><meta charset=\"utf-8\"><title>Guide</title></head><body><p>Café menu</p></body></html>");

        Assert.True(DocumentReader.IsSupported(path));
        var part = Assert.Single(DocumentReader.Read(path));
        Assert.Equal("Guide", part.Location);
        Assert.Equal("Café menu", part.Text);
    }

    [Fact]
    public void Pdf_is_read_page_by_page()
    {
        using var folder = new TempFolder();
        var path = folder.Combine("policy.pdf");

        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(PageSize.A4).AddText("Invoices are due in 30 days.", 12, new PdfPoint(25, 700), font);
        builder.AddPage(PageSize.A4).AddText("Refunds take five business days.", 12, new PdfPoint(25, 700), font);
        File.WriteAllBytes(path, builder.Build());

        var parts = DocumentReader.Read(path);

        Assert.Equal(["page 1", "page 2"], parts.Select(part => part.Location));
        Assert.Contains("Refunds take five business days.", parts[1].Text);
    }

    [Theory]
    [InlineData("guide.md", true)]
    [InlineData("REPORT.PDF", true)]
    [InlineData("notes.txt", true)]
    [InlineData("Program.cs", false)]
    [InlineData("contract.docx", false)]
    public void Supported_file_types(string fileName, bool supported)
    {
        Assert.Equal(supported, DocumentReader.IsSupported(fileName));
    }
}
