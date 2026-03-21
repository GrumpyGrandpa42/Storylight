using StoryLight.App.Models;

namespace StoryLight.App.Services;

public interface IDocumentImporter
{
    Task<NormalizedDocument> ImportAsync(string path, CancellationToken cancellationToken = default);
}
