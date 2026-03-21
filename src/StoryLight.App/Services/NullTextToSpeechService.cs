using StoryLight.App.Models;

namespace StoryLight.App.Services;

public sealed class NullTextToSpeechService : ITextToSpeechService
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
    public string StatusSummary => "sherpa-onnx voices are only available on Windows.";
    public IReadOnlyList<TtsVoiceInfo> Voices => Array.Empty<TtsVoiceInfo>();

    public void Dispose()
    {
    }

    public Task InitializeAsync(SpeechSettings settings, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task RefreshAsync(CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task SetVoiceAsync(string? voiceId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task SpeakAsync(string text, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task PauseAsync() => Task.CompletedTask;

    public Task ResumeAsync() => Task.CompletedTask;

    public Task StopAsync() => Task.CompletedTask;
}
