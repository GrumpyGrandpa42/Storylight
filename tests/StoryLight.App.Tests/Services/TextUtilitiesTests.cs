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
    public void EstimatePageMetrics_ShrinkInBothDimensionsAsZoomIncreases()
    {
        Assert.Equal(34, TextUtilities.EstimateLinesPerPage(1.0));
        Assert.Equal(72, TextUtilities.EstimateCharactersPerLine(1.0));
        Assert.Equal(17, TextUtilities.EstimateLinesPerPage(2.0));
        Assert.Equal(36, TextUtilities.EstimateCharactersPerLine(2.0));
    }

    [Fact]
    public void PaginatePlainText_SplitsParagraphsByEstimatedWrappedLines()
    {
        const string text = """
            alpha beta gamma delta epsilon zeta

            one two three four five six

            red blue green yellow orange purple
            """;

        var pages = TextUtilities.PaginatePlainText(text, linesPerPage: 4, charactersPerLine: 12);

        Assert.Collection(
            pages,
            page => Assert.Equal("alpha beta gamma delta epsilon zeta", page),
            page => Assert.Equal("one two three four five six\n\nred blue green yellow orange purple", page));
    }

    [Fact]
    public void PaginatePlainText_SplitsOversizedParagraphIntoMultiplePages()
    {
        const string text = "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu";

        var pages = TextUtilities.PaginatePlainText(text, linesPerPage: 2, charactersPerLine: 12);

        Assert.True(pages.Count >= 2);
        Assert.All(pages, page => Assert.False(string.IsNullOrWhiteSpace(page)));
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
