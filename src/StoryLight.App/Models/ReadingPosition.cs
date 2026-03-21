namespace StoryLight.App.Models;

public sealed class ReadingPosition
{
    public string DocumentId { get; set; } = string.Empty;
    public int PageIndex { get; set; }
    public int SectionIndex { get; set; }
    public double ProgressPercent { get; set; }
    public double ZoomLevel { get; set; } = 1.0;
}
