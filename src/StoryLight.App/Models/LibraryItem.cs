using System.IO;
using System.Text.Json.Serialization;
using Avalonia.Media.Imaging;
using StoryLight.App.Utils;

namespace StoryLight.App.Models;

public sealed class LibraryItem : ObservableObject
{
    private string _title = string.Empty;
    private string _subtitle = string.Empty;
    private double _progressPercent;
    private DateTimeOffset _lastOpenedUtc;
    private byte[]? _coverImageData;
    private Bitmap? _coverBitmap;

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

    public byte[]? CoverImageData
    {
        get => _coverImageData;
        set
        {
            if (SetProperty(ref _coverImageData, value))
            {
                RebuildCoverBitmap();
                RaisePropertyChanged(nameof(HasCoverImage));
            }
        }
    }

    [JsonIgnore]
    public Bitmap? CoverBitmap
    {
        get => _coverBitmap;
        private set => SetProperty(ref _coverBitmap, value);
    }

    [JsonIgnore]
    public bool HasCoverImage => CoverBitmap is not null;

    [JsonIgnore]
    public bool HasNoCoverImage => !HasCoverImage;

    public string ProgressText => $"{ProgressPercent:P0}";
    public string LastOpenedDisplay => LastOpenedUtc == default ? "Not opened yet" : $"Opened {LastOpenedUtc.LocalDateTime:g}";
    public string FormatLabel => Format.ToString().ToUpperInvariant();

    public void RefreshDerivedProperties()
    {
        RebuildCoverBitmap();
        RaisePropertyChanged(nameof(ProgressText));
        RaisePropertyChanged(nameof(LastOpenedDisplay));
        RaisePropertyChanged(nameof(FormatLabel));
        RaisePropertyChanged(nameof(HasCoverImage));
        RaisePropertyChanged(nameof(HasNoCoverImage));
    }

    private void RebuildCoverBitmap()
    {
        CoverBitmap?.Dispose();
        CoverBitmap = null;

        if (_coverImageData is null || _coverImageData.Length == 0)
        {
            return;
        }

        try
        {
            using var stream = new MemoryStream(_coverImageData, writable: false);
            CoverBitmap = new Bitmap(stream);
        }
        catch
        {
            CoverBitmap = null;
        }
    }
}
