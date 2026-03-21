namespace StoryLight.App.Models;

public sealed class AppState
{
    public List<LibraryItem> Library { get; set; } = new();
    public List<ReadingPosition> ReadingPositions { get; set; } = new();
    public double DefaultZoomLevel { get; set; } = 1.0;
}
