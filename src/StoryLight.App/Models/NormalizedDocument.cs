namespace StoryLight.App.Models;

public sealed class NormalizedDocument
{
    public required string SourcePath { get; init; }
    public required DocumentMetadata Metadata { get; init; }
    public required IReadOnlyList<DocumentSection> Sections { get; init; }
}
