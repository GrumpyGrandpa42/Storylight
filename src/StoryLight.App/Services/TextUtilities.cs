using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace StoryLight.App.Services;

internal static partial class TextUtilities
{
    private const int MinimumEstimatedLinesPerPage = 10;
    private const int MinimumEstimatedCharactersPerLine = 20;

    public static string NormalizeText(string text)
    {
        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('\u00A0', ' ');

        normalized = Regex.Replace(normalized, @"[ \t]+\n", "\n");
        normalized = Regex.Replace(normalized, @"\n[ \t]+", "\n");
        normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n");
        return normalized.Trim();
    }

    public static string StripMarkdown(string markdown)
    {
        var text = markdown
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        text = MarkdownImageRegex().Replace(text, string.Empty);
        text = MarkdownLinkRegex().Replace(text, "$1");
        text = MarkdownCodeFenceRegex().Replace(text, string.Empty);
        text = MarkdownInlineCodeRegex().Replace(text, "$1");
        text = MarkdownHeadingRegex().Replace(text, string.Empty);
        text = MarkdownListRegex().Replace(text, string.Empty);
        text = MarkdownEmphasisRegex().Replace(text, "$1");
        text = text.Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase);

        return NormalizeText(text);
    }

    public static string HtmlToPlainText(string html)
    {
        try
        {
            var document = XDocument.Parse(html, LoadOptions.PreserveWhitespace);
            var builder = new StringBuilder();
            var startNode = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "body") ?? document.Root;
            AppendNodeText(startNode, builder);
            return NormalizeText(WebUtility.HtmlDecode(builder.ToString()));
        }
        catch
        {
            var withoutHead = HtmlHeadRegex().Replace(html, " ");
            var withoutScripts = HtmlScriptOrStyleRegex().Replace(withoutHead, " ");
            var text = HtmlTagRegex().Replace(withoutScripts, " ");
            text = WebUtility.HtmlDecode(text);
            return NormalizeText(text);
        }
    }

    public static string BuildDocxText(string xml)
    {
        var document = XDocument.Parse(xml);
        XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var paragraphs = document
            .Descendants(word + "p")
            .Select(paragraph =>
            {
                var textRuns = paragraph.Descendants(word + "t").Select(run => run.Value);
                return string.Concat(textRuns);
            })
            .Where(text => !string.IsNullOrWhiteSpace(text));

        return NormalizeText(string.Join(Environment.NewLine + Environment.NewLine, paragraphs));
    }

    public static string? ExtractCoreProperty(string xml, string localName)
    {
        var document = XDocument.Parse(xml);
        return document.Descendants().FirstOrDefault(element => element.Name.LocalName == localName)?.Value?.Trim();
    }

    public static int EstimateLinesPerPage(double zoomLevel)
    {
        var safeZoom = Math.Max(0.25d, zoomLevel);
        return Math.Max(MinimumEstimatedLinesPerPage, (int)Math.Round(34d / safeZoom));
    }

    public static int EstimateCharactersPerLine(double zoomLevel)
    {
        var safeZoom = Math.Max(0.25d, zoomLevel);
        return Math.Max(MinimumEstimatedCharactersPerLine, (int)Math.Round(72d / safeZoom));
    }

    public static IReadOnlyList<string> PaginatePlainText(string text, int linesPerPage, int charactersPerLine)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(linesPerPage);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(charactersPerLine);

        var paragraphs = text.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var pages = new List<string>();
        var currentPage = new List<string>();
        var currentLineCount = 0;

        foreach (var paragraph in paragraphs)
        {
            var paragraphLines = EstimateWrappedLineCount(paragraph, charactersPerLine);

            if (paragraphLines > linesPerPage)
            {
                if (currentPage.Count > 0)
                {
                    pages.Add(string.Join(Environment.NewLine + Environment.NewLine, currentPage));
                    currentPage.Clear();
                    currentLineCount = 0;
                }

                foreach (var chunk in SplitLongParagraph(paragraph, linesPerPage, charactersPerLine))
                {
                    pages.Add(chunk);
                }

                continue;
            }

            var separatorLines = currentPage.Count == 0 ? 0 : 1;
            if (currentLineCount > 0 && currentLineCount + separatorLines + paragraphLines > linesPerPage)
            {
                pages.Add(string.Join(Environment.NewLine + Environment.NewLine, currentPage));
                currentPage.Clear();
                currentLineCount = 0;
                separatorLines = 0;
            }

            currentPage.Add(paragraph);
            currentLineCount += separatorLines + paragraphLines;
        }

        if (currentPage.Count > 0)
        {
            pages.Add(string.Join(Environment.NewLine + Environment.NewLine, currentPage));
        }

        return pages;
    }

    private static void AppendNodeText(XNode? node, StringBuilder builder)
    {
        if (node is null)
        {
            return;
        }

        if (node is XText text)
        {
            builder.Append(text.Value);
            return;
        }

        if (node is not XElement element)
        {
            return;
        }

        var isBlock = element.Name.LocalName is "p" or "div" or "section" or "article" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "li" or "tr" or "blockquote";
        if (isBlock && builder.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine();
        }

        foreach (var child in element.Nodes())
        {
            AppendNodeText(child, builder);
        }
    }

    private static int EstimateWrappedLineCount(string paragraph, int charactersPerLine)
    {
        if (string.IsNullOrWhiteSpace(paragraph))
        {
            return 0;
        }

        var lines = 0;
        foreach (var rawLine in paragraph.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                lines++;
                continue;
            }

            var currentLineLength = 0;
            foreach (var word in line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var wordLength = word.Length;
                if (currentLineLength == 0)
                {
                    currentLineLength = wordLength % charactersPerLine;
                    lines += Math.Max(1, (int)Math.Ceiling(wordLength / (double)charactersPerLine)) - 1;
                    if (currentLineLength == 0)
                    {
                        currentLineLength = charactersPerLine;
                    }

                    continue;
                }

                if (currentLineLength + 1 + wordLength <= charactersPerLine)
                {
                    currentLineLength += 1 + wordLength;
                    continue;
                }

                lines++;
                currentLineLength = wordLength % charactersPerLine;
                lines += Math.Max(1, (int)Math.Ceiling(wordLength / (double)charactersPerLine)) - 1;
                if (currentLineLength == 0)
                {
                    currentLineLength = charactersPerLine;
                }
            }

            if (currentLineLength > 0)
            {
                lines++;
            }
        }

        return lines;
    }

    private static IEnumerable<string> SplitLongParagraph(string paragraph, int linesPerPage, int charactersPerLine)
    {
        var tokens = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(word => SplitWordForWrapping(word, linesPerPage * charactersPerLine))
            .ToArray();

        if (tokens.Length == 0)
        {
            yield break;
        }

        var currentWords = new List<string>();
        foreach (var token in tokens)
        {
            currentWords.Add(token);
            var candidate = string.Join(' ', currentWords);
            if (EstimateWrappedLineCount(candidate, charactersPerLine) <= linesPerPage)
            {
                continue;
            }

            if (currentWords.Count == 1)
            {
                yield return currentWords[0];
                currentWords.Clear();
                continue;
            }

            currentWords.RemoveAt(currentWords.Count - 1);
            yield return string.Join(' ', currentWords);
            currentWords.Clear();
            currentWords.Add(token);
        }

        if (currentWords.Count > 0)
        {
            yield return string.Join(' ', currentWords);
        }
    }

    private static IEnumerable<string> SplitWordForWrapping(string word, int maxChunkLength)
    {
        if (word.Length <= maxChunkLength)
        {
            yield return word;
            yield break;
        }

        for (var start = 0; start < word.Length; start += maxChunkLength)
        {
            yield return word.Substring(start, Math.Min(maxChunkLength, word.Length - start));
        }
    }

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]+\)", RegexOptions.Compiled)]
    private static partial Regex MarkdownImageRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)", RegexOptions.Compiled)]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"```[\s\S]*?```", RegexOptions.Compiled)]
    private static partial Regex MarkdownCodeFenceRegex();

    [GeneratedRegex(@"`([^`]+)`", RegexOptions.Compiled)]
    private static partial Regex MarkdownInlineCodeRegex();

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s*", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex MarkdownHeadingRegex();

    [GeneratedRegex(@"^\s*[-*+]\s+", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex MarkdownListRegex();

    [GeneratedRegex(@"(\*\*|__|\*|_)(.*?)\1", RegexOptions.Compiled)]
    private static partial Regex MarkdownEmphasisRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"<head\b[^>]*>[\s\S]*?</head>", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex HtmlHeadRegex();

    [GeneratedRegex(@"<(script|style)\b[^>]*>[\s\S]*?</\1>", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex HtmlScriptOrStyleRegex();
}
