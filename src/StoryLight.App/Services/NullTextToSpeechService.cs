namespace StoryLight.App.Services;

public sealed class NullTextToSpeechService : ITextToSpeechService
{
    public bool IsAvailable => false;
    public bool IsSpeaking => false;
    public bool IsPaused => false;
    public int Rate { get; set; }

    public void Dispose()
    {
    }

    public Task SpeakAsync(string text, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task PauseAsync() => Task.CompletedTask;

    public Task ResumeAsync() => Task.CompletedTask;

    public Task StopAsync() => Task.CompletedTask;
}
