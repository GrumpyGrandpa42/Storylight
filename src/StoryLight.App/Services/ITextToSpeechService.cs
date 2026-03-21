namespace StoryLight.App.Services;

public interface ITextToSpeechService : IDisposable
{
    bool IsAvailable { get; }
    bool IsSpeaking { get; }
    bool IsPaused { get; }
    int Rate { get; set; }

    Task SpeakAsync(string text, CancellationToken cancellationToken = default);
    Task PauseAsync();
    Task ResumeAsync();
    Task StopAsync();
}
