using StoryLight.App.Services;
using Xunit;

namespace StoryLight.App.Tests.Services;

public sealed class TextUtilitiesTests
{
    [Fact]
    public void NormalizeText_NormalizesLineEndingsAndBlankRuns()
    {
        const string input = "  First line  \r\nSecond line\t \r\r\n\r\nThird line \n\n\n";

        var result = TextUtilities.NormalizeText(input);

        Assert.Equal("First line\nSecond line\n\nThird line", result);
    }

    [Fact]
    public void StripMarkdown_RemovesMarkdownFormatting()
    {
        const string markdown = """
            # Chapter One

            - A [linked](https://example.com) item
            - `inline code`
            """;

        var result = TextUtilities.StripMarkdown(markdown);

        Assert.Equal("Chapter One\nA linked item\ninline code", result);
    }

    [Fact]
    public void HtmlToPlainText_UsesBodyContentAndDecodesEntities()
    {
        const string html = """
            <html>
              <head>
                <title>Ignored</title>
              </head>
              <body>
                <h1>Chapter&nbsp;1</h1>
                <p>Hello &amp; goodbye.</p>
              </body>
            </html>
            """;

        var result = TextUtilities.HtmlToPlainText(html);

        Assert.Equal("Chapter 1\nHello & goodbye.", result);
    }

    [Fact]
    public void BuildDocxText_JoinsParagraphs()
    {
        const string xml = """
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body>
                <w:p><w:r><w:t>First</w:t></w:r></w:p>
                <w:p><w:r><w:t>Second</w:t></w:r></w:p>
              </w:body>
            </w:document>
            """;

        var result = TextUtilities.BuildDocxText(xml);

        Assert.Equal("First\n\nSecond", result);
    }
}
