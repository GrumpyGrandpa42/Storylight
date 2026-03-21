using StoryLight.App.Models;

namespace StoryLight.App.Services;

public interface ITextToSpeechService : IDisposable
{
    event EventHandler? PlaybackCompleted;

    bool IsAvailable { get; }
    bool IsSpeaking { get; }
    bool IsPaused { get; }
    int Rate { get; set; }
    string? SelectedVoiceId { get; }
    string VoicesFolderPath { get; }
    string StatusSummary { get; }
    IReadOnlyList<TtsVoiceInfo> Voices { get; }

    Task InitializeAsync(SpeechSettings settings, CancellationToken cancellationToken = default);
    Task RefreshAsync(CancellationToken cancellationToken = default);
    Task SetVoiceAsync(string? voiceId, CancellationToken cancellationToken = default);
    Task PrefetchAsync(string text, CancellationToken cancellationToken = default);
    Task SpeakAsync(string text, CancellationToken cancellationToken = default);
    Task PauseAsync();
    Task ResumeAsync();
    Task StopAsync();
}
