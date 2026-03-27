using StoryLight.App.Models;
using StoryLight.App.Services;
using StoryLight.App.ViewModels;
using Xunit;

namespace StoryLight.App.Tests.ViewModels;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void Constructor_PreservesSavedLibraryOrder()
    {
        var first = CreateLibraryItem("first", hoursAgo: 1);
        var second = CreateLibraryItem("second", hoursAgo: 10);
        var third = CreateLibraryItem("third", hoursAgo: 2);
        var state = new AppState
        {
            Library = [second, first, third]
        };

        using var viewModel = CreateViewModel(state, out _);

        Assert.Collection(
            viewModel.LibraryItems,
            item => Assert.Same(second, item),
            item => Assert.Same(first, item),
            item => Assert.Same(third, item));
    }

    [Fact]
    public async Task MoveLibraryItemAsync_UpdatesVisibleAndSavedOrder()
    {
        var first = CreateLibraryItem("first");
        var second = CreateLibraryItem("second");
        var third = CreateLibraryItem("third");
        var state = new AppState
        {
            Library = [first, second, third]
        };

        using var viewModel = CreateViewModel(state, out var stateStore);

        await viewModel.MoveLibraryItemAsync(first, third, insertAfter: true);

        Assert.Collection(
            viewModel.LibraryItems,
            item => Assert.Same(second, item),
            item => Assert.Same(third, item),
            item => Assert.Same(first, item));

        Assert.NotNull(stateStore.LastSavedState);
        Assert.Collection(
            stateStore.LastSavedState!.Library,
            item => Assert.Same(second, item),
            item => Assert.Same(third, item),
            item => Assert.Same(first, item));
    }

    private static MainWindowViewModel CreateViewModel(AppState state, out FakeReaderStateStore stateStore)
    {
        stateStore = new FakeReaderStateStore(state);
        return new MainWindowViewModel(
            new FakeDocumentImporter(),
            stateStore,
            new FakeTextToSpeechService());
    }

    private static LibraryItem CreateLibraryItem(string id, int hoursAgo = 0)
    {
        return new LibraryItem
        {
            Id = id,
            SourcePath = $@"C:\Books\{id}.epub",
            Format = DocumentFormat.Epub,
            Title = id,
            Subtitle = $"{id} subtitle",
            LastOpenedUtc = DateTimeOffset.UtcNow.AddHours(-hoursAgo)
        };
    }

    private sealed class FakeReaderStateStore : IReaderStateStore
    {
        private readonly AppState _state;

        public FakeReaderStateStore(AppState state)
        {
            _state = state;
        }

        public AppState? LastSavedState { get; private set; }

        public Task<AppState> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_state);
        }

        public Task SaveAsync(AppState state, CancellationToken cancellationToken = default)
        {
            LastSavedState = new AppState
            {
                Library = state.Library.ToList(),
                ReadingPositions = state.ReadingPositions.ToList(),
                DefaultZoomLevel = state.DefaultZoomLevel,
                IsLibraryCollapsed = state.IsLibraryCollapsed,
                Speech = state.Speech
            };

            return Task.CompletedTask;
        }
    }

    private sealed class FakeDocumentImporter : IDocumentImporter
    {
        public Task<NormalizedDocument> ImportAsync(string path, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeTextToSpeechService : ITextToSpeechService
    {
        public event EventHandler? PlaybackCompleted
        {
            add { }
            remove { }
        }

        public bool IsAvailable => false;
        public bool IsSpeaking => false;
        public bool IsPaused => false;
        public int Rate { get; set; }
        public string? SelectedVoiceId => null;
        public string VoicesFolderPath => string.Empty;
        public string StatusSummary => "Text to speech unavailable.";
        public IReadOnlyList<TtsVoiceInfo> Voices { get; } = [];

        public void Dispose()
        {
        }

        public Task InitializeAsync(SpeechSettings settings, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SetVoiceAsync(string? voiceId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task PrefetchAsync(string text, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task SpeakAsync(string text, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task PauseAsync()
        {
            return Task.CompletedTask;
        }

        public Task ResumeAsync()
        {
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            return Task.CompletedTask;
        }
    }
}
