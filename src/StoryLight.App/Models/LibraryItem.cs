using StoryLight.App.Utils;

namespace StoryLight.App.Models;

public sealed class LibraryItem : ObservableObject
{
    private string _title = string.Empty;
    private string _subtitle = string.Empty;
    private double _progressPercent;
    private DateTimeOffset _lastOpenedUtc;

    public required string Id { get; init; }
    public required string SourcePath { get; init; }
    public required DocumentFormat Format { get; init; }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        set => SetProperty(ref _subtitle, value);
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        set => SetProperty(ref _progressPercent, value);
    }

    public DateTimeOffset LastOpenedUtc
    {
        get => _lastOpenedUtc;
        set => SetProperty(ref _lastOpenedUtc, value);
    }

    public string ProgressText => $"{ProgressPercent:P0}";
    public string LastOpenedDisplay => LastOpenedUtc == default ? "Not opened yet" : $"Opened {LastOpenedUtc.LocalDateTime:g}";

    public void RefreshDerivedProperties()
    {
        RaisePropertyChanged(nameof(ProgressText));
        RaisePropertyChanged(nameof(LastOpenedDisplay));
    }
}
