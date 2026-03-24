using StoryLight.App.Utils;

namespace StoryLight.App.Models;

public sealed class ChapterNavigationItem : ObservableObject
{
    private bool _isCurrent;

    public ChapterNavigationItem(string title, int sectionIndex, RelayCommand activateCommand)
    {
        Title = title;
        SectionIndex = sectionIndex;
        ActivateCommand = activateCommand;
    }

    public string Title { get; }

    public int SectionIndex { get; }

    public RelayCommand ActivateCommand { get; }

    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }
}
