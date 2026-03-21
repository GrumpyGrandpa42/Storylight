using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace StoryLight.App.Services;

internal static partial class TextUtilities
{
    public static string NormalizeText(string text)
    {
        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        normalized = Regex.Replace(normalized, @"[ \t]+\n", "\n");
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
