using System.IO.Compression;
using System.Xml.Linq;
using StoryLight.App.Models;
using UglyToad.PdfPig;

namespace StoryLight.App.Services;

public sealed class DocumentImportService : IDocumentImporter
{
    public async Task<NormalizedDocument> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();

        return extension switch
        {
            ".txt" => await ImportTextAsync(path, cancellationToken),
            ".md" => await ImportMarkdownAsync(path, cancellationToken),
            ".docx" => await ImportDocxAsync(path, path, cancellationToken),
            ".doc" => await ImportLegacyDocAsync(path, cancellationToken),
            ".epub" => await ImportEpubAsync(path, cancellationToken),
            ".pdf" => await ImportPdfAsync(path, cancellationToken),
            _ => throw new NotSupportedException($"Unsupported file type: {extension}")
        };
    }

    private static async Task<NormalizedDocument> ImportTextAsync(string path, CancellationToken cancellationToken)
    {
        var text = TextUtilities.NormalizeText(await File.ReadAllTextAsync(path, cancellationToken));
        return BuildSingleSectionDocument(path, Path.GetFileNameWithoutExtension(path), null, DocumentFormat.Text, text);
    }

    private static async Task<NormalizedDocument> ImportMarkdownAsync(string path, CancellationToken cancellationToken)
    {
        var markdown = await File.ReadAllTextAsync(path, cancellationToken);
        var text = TextUtilities.StripMarkdown(markdown);
        return BuildSingleSectionDocument(path, Path.GetFileNameWithoutExtension(path), null, DocumentFormat.Markdown, text);
    }

    private static async Task<NormalizedDocument> ImportDocxAsync(string path, string originalSourcePath, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(path);
        var documentEntry = archive.GetEntry("word/document.xml")
            ?? throw new InvalidDataException("The DOCX file does not contain word/document.xml.");

        var documentXml = await ReadEntryAsync(documentEntry, cancellationToken);
        var text = TextUtilities.BuildDocxText(documentXml);

        string? title = null;
        string? author = null;

        var coreEntry = archive.GetEntry("docProps/core.xml");
        if (coreEntry is not null)
        {
            var coreXml = await ReadEntryAsync(coreEntry, cancellationToken);
            title = TextUtilities.ExtractCoreProperty(coreXml, "title");
            author = TextUtilities.ExtractCoreProperty(coreXml, "creator");
        }

        return BuildSingleSectionDocument(
            originalSourcePath,
            string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(originalSourcePath) : title,
            author,
            Path.GetExtension(originalSourcePath).Equals(".doc", StringComparison.OrdinalIgnoreCase) ? DocumentFormat.Doc : DocumentFormat.Docx,
            text);
    }

    private static async Task<NormalizedDocument> ImportLegacyDocAsync(string path, CancellationToken cancellationToken)
    {
        var tempDocxPath = Path.Combine(Path.GetTempPath(), $"storylight-{Guid.NewGuid():N}.docx");
        try
        {
            var converted = await WordAutomationDocumentConverter.TryConvertDocToDocxAsync(path, tempDocxPath, cancellationToken);
            if (!converted)
            {
                throw new InvalidOperationException("Word fallback conversion failed. Make sure Microsoft Word is installed.");
            }

            return await ImportDocxAsync(tempDocxPath, path, cancellationToken);
        }
        finally
        {
            if (File.Exists(tempDocxPath))
            {
                File.Delete(tempDocxPath);
            }
        }
    }

    private static async Task<NormalizedDocument> ImportEpubAsync(string path, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(path);
        var containerEntry = archive.GetEntry("META-INF/container.xml")
            ?? throw new InvalidDataException("The EPUB file is missing META-INF/container.xml.");

        var containerXml = await ReadEntryAsync(containerEntry, cancellationToken);
        var container = XDocument.Parse(containerXml);
        var rootFilePath = container.Descendants().FirstOrDefault(element => element.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value
            ?? throw new InvalidDataException("The EPUB container did not provide a package document.");

        var packageEntry = archive.GetEntry(rootFilePath.Replace('\\', '/'))
            ?? throw new InvalidDataException("The EPUB package document could not be found.");

        var packageXml = await ReadEntryAsync(packageEntry, cancellationToken);
        var packageDocument = XDocument.Parse(packageXml);
        var packageDirectory = Path.GetDirectoryName(rootFilePath)?.Replace('\\', '/') ?? string.Empty;

        var title = packageDocument.Descendants().FirstOrDefault(element => element.Name.LocalName == "title")?.Value?.Trim();
        var author = packageDocument.Descendants().FirstOrDefault(element => element.Name.LocalName == "creator")?.Value?.Trim();

        var manifest = packageDocument
            .Descendants()
            .Where(element => element.Name.LocalName == "item")
            .Select(element => new
            {
                Id = element.Attribute("id")?.Value,
                Href = element.Attribute("href")?.Value
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Href))
            .ToDictionary(item => item.Id!, item => item.Href!);

        var sections = new List<DocumentSection>();
        var sectionIndex = 0;

        foreach (var itemRef in packageDocument.Descendants().Where(element => element.Name.LocalName == "itemref"))
        {
            var idRef = itemRef.Attribute("idref")?.Value;
            if (string.IsNullOrWhiteSpace(idRef) || !manifest.TryGetValue(idRef, out var href))
            {
                continue;
            }

            var entryPath = CombineArchivePath(packageDirectory, href);
            var entry = archive.GetEntry(entryPath);
            if (entry is null)
            {
                continue;
            }

            var markup = await ReadEntryAsync(entry, cancellationToken);
            var plainText = TextUtilities.HtmlToPlainText(markup);
            if (string.IsNullOrWhiteSpace(plainText))
            {
                continue;
            }

            var sectionTitle = TryGetHtmlTitle(markup) ?? $"Section {sectionIndex + 1}";
            sections.Add(new DocumentSection(sectionTitle, plainText, $"section-{sectionIndex}"));
            sectionIndex++;
        }

        if (sections.Count == 0)
        {
            throw new InvalidDataException("No readable sections were found in the EPUB.");
        }

        var coverImageData = await TryReadEpubCoverAsync(archive, packageDocument, manifest, packageDirectory, cancellationToken);

        return new NormalizedDocument
        {
            SourcePath = path,
            Metadata = new DocumentMetadata(
                string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(path) : title,
                author,
                DocumentFormat.Epub,
                coverImageData),
            Sections = sections
        };
    }

    private static Task<NormalizedDocument> ImportPdfAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var document = PdfDocument.Open(path);
        var sections = document.GetPages()
            .Select((page, index) => new DocumentSection(
                $"Page {index + 1}",
                TextUtilities.NormalizeText(page.Text),
                $"page-{index}",
                PreserveAsPage: true))
            .Where(section => !string.IsNullOrWhiteSpace(section.Text))
            .ToList();

        if (sections.Count == 0)
        {
            throw new InvalidDataException("The PDF did not contain extractable text.");
        }

        var info = document.Information;
        return Task.FromResult(new NormalizedDocument
        {
            SourcePath = path,
            Metadata = new DocumentMetadata(
                string.IsNullOrWhiteSpace(info.Title) ? Path.GetFileNameWithoutExtension(path) : info.Title,
                string.IsNullOrWhiteSpace(info.Author) ? null : info.Author,
                DocumentFormat.Pdf),
            Sections = sections
        });
    }

    private static NormalizedDocument BuildSingleSectionDocument(string path, string title, string? author, DocumentFormat format, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidDataException("The document did not contain readable text.");
        }

        return new NormalizedDocument
        {
            SourcePath = path,
            Metadata = new DocumentMetadata(title, author, format),
            Sections = new[]
            {
                new DocumentSection(title, text, "section-0")
            }
        };
    }

    private static async Task<string> ReadEntryAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static async Task<byte[]?> TryReadEpubCoverAsync(
        ZipArchive archive,
        XDocument packageDocument,
        IReadOnlyDictionary<string, string> manifest,
        string packageDirectory,
        CancellationToken cancellationToken)
    {
        var manifestItems = packageDocument
            .Descendants()
            .Where(element => element.Name.LocalName == "item")
            .ToList();

        var coverId = packageDocument
            .Descendants()
            .FirstOrDefault(element =>
                element.Name.LocalName == "meta"
                && string.Equals(element.Attribute("name")?.Value, "cover", StringComparison.OrdinalIgnoreCase))
            ?.Attribute("content")
            ?.Value;

        string? coverHref = null;

        if (!string.IsNullOrWhiteSpace(coverId) && manifest.TryGetValue(coverId, out var hrefFromMeta))
        {
            coverHref = hrefFromMeta;
        }

        if (string.IsNullOrWhiteSpace(coverHref))
        {
            coverHref = manifestItems
                .FirstOrDefault(item =>
                    item.Attribute("properties")?.Value?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Contains("cover-image", StringComparer.OrdinalIgnoreCase) == true)
                ?.Attribute("href")
                ?.Value;
        }

        if (string.IsNullOrWhiteSpace(coverHref))
        {
            coverHref = manifestItems
                .FirstOrDefault(item =>
                {
                    var id = item.Attribute("id")?.Value;
                    var href = item.Attribute("href")?.Value;
                    return (!string.IsNullOrWhiteSpace(id) && id.Contains("cover", StringComparison.OrdinalIgnoreCase))
                        || (!string.IsNullOrWhiteSpace(href) && href.Contains("cover", StringComparison.OrdinalIgnoreCase));
                })
                ?.Attribute("href")
                ?.Value;
        }

        if (string.IsNullOrWhiteSpace(coverHref))
        {
            return null;
        }

        var entryPath = CombineArchivePath(packageDirectory, coverHref);
        var entry = archive.GetEntry(entryPath);
        if (entry is null)
        {
            return null;
        }

        await using var stream = entry.Open();
        using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream, cancellationToken);
        return memoryStream.ToArray();
    }

    private static string CombineArchivePath(string baseDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return relativePath.Replace('\\', '/');
        }

        return $"{baseDirectory.TrimEnd('/')}/{relativePath.TrimStart('/')}".Replace('\\', '/');
    }

    private static string? TryGetHtmlTitle(string markup)
    {
        try
        {
            var document = XDocument.Parse(markup);
            return document
                .Descendants()
                .FirstOrDefault(element => element.Name.LocalName is "h1" or "h2" or "title")
                ?.Value
                .Trim();
        }
        catch
        {
            return null;
        }
    }
}
