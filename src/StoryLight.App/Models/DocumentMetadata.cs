namespace StoryLight.App.Models;

public sealed record DocumentMetadata(string Title, string? Author, DocumentFormat Format, byte[]? CoverImageData = null);
