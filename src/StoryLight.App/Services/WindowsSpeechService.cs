using System.Runtime.Versioning;

namespace StoryLight.App.Services;

[SupportedOSPlatform("windows")]
public sealed class WindowsSpeechService : ITextToSpeechService
{
    private const int SpeakFlagsDefault = 0;
    private const int SpeakFlagsAsync = 1;
    private const int SpeakFlagsPurgeBeforeSpeak = 2;

    private readonly SemaphoreSlim _mutex = new(1, 1);
    private dynamic? _voice;
    private bool _disposed;

    public bool IsAvailable => OperatingSystem.IsWindows();
    public bool IsSpeaking { get; private set; }
    public bool IsPaused { get; private set; }
    public int Rate { get; set; }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            StopCore();

            var voice = EnsureVoice();
            voice.Rate = Rate;
            voice.Speak(text, SpeakFlagsAsync);
            IsSpeaking = true;
            IsPaused = false;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task PauseAsync()
    {
        await _mutex.WaitAsync();
        try
        {
            if (_voice is not null && IsSpeaking && !IsPaused)
            {
                _voice.Pause();
                IsPaused = true;
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task ResumeAsync()
    {
        await _mutex.WaitAsync();
        try
        {
            if (_voice is not null && IsPaused)
            {
                _voice.Resume();
                IsPaused = false;
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task StopAsync()
    {
        await _mutex.WaitAsync();
        try
        {
            StopCore();
        }
        finally
        {
            _mutex.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopCore();
        _mutex.Dispose();
    }

    [SupportedOSPlatform("windows")]
    private dynamic EnsureVoice()
    {
        if (_voice is not null)
        {
            return _voice;
        }

        var type = Type.GetTypeFromProgID("SAPI.SpVoice")
            ?? throw new InvalidOperationException("Windows speech is unavailable.");

        _voice = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Unable to create Windows speech voice.");
        return _voice;
    }

    private void StopCore()
    {
        if (_voice is not null)
        {
            try
            {
                _voice.Speak(string.Empty, SpeakFlagsPurgeBeforeSpeak);
            }
            catch
            {
            }
        }

        IsSpeaking = false;
        IsPaused = false;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsSpeechService));
        }
    }
}
