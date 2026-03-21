using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using StoryLight.App.Models;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace StoryLight.App.Services;

public sealed class WindowsSpeechService : ITextToSpeechService
{
    private static readonly TimeSpan LeadInDuration = TimeSpan.FromMilliseconds(250);

    public event EventHandler? PlaybackCompleted;

    private readonly SemaphoreSlim _mutex = new(1, 1);
    private SpeechSynthesizer? _synthesizer;
    private CancellationTokenSource? _prefetchCts;
    private CancellationTokenSource? _playbackMonitorCts;
    private WaveOutEvent? _waveOut;
    private WaveFileReader? _waveReader;
    private MemoryStream? _audioBuffer;
    private PreparedSpeech? _preparedSpeech;
    private bool _disposed;
    private int _rate;
    private IReadOnlyList<TtsVoiceInfo> _voices = Array.Empty<TtsVoiceInfo>();

    public bool IsAvailable => OperatingSystem.IsWindows() && _voices.Count > 0;
    public bool IsSpeaking { get; private set; }
    public bool IsPaused { get; private set; }

    public int Rate
    {
        get => _rate;
        set => _rate = Math.Clamp(value, -3, 3);
    }

    public string? SelectedVoiceId { get; private set; }
    public string VoicesFolderPath => string.Empty;
    public string StatusSummary => _voices.Count > 0
        ? "Windows voices are available."
        : "No Windows voices are available.";
    public IReadOnlyList<TtsVoiceInfo> Voices => _voices;

    public async Task InitializeAsync(SpeechSettings settings, CancellationToken cancellationToken = default)
    {
        Rate = settings.Rate;
        SelectedVoiceId = settings.VoiceId;
        await RefreshAsync(cancellationToken);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindows())
        {
            _voices = Array.Empty<TtsVoiceInfo>();
            SelectedVoiceId = null;
            return;
        }

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();

            var synthesizer = EnsureSynthesizer();
            _voices = SpeechSynthesizer.AllVoices
                .Select(voice => new TtsVoiceInfo(voice.Id, BuildVoiceDisplayName(voice)))
                .OrderBy(voice => voice.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();

            if (_voices.Count == 0)
            {
                SelectedVoiceId = null;
                _preparedSpeech = null;
                return;
            }

            if (string.IsNullOrWhiteSpace(SelectedVoiceId) || !_voices.Any(voice => voice.Id == SelectedVoiceId))
            {
                SelectedVoiceId = _voices[0].Id;
            }

            ApplySelectedVoice(synthesizer, SelectedVoiceId);
            ApplyRate(synthesizer, Rate);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task SetVoiceAsync(string? voiceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            SelectedVoiceId = _voices.FirstOrDefault(voice => voice.Id == voiceId)?.Id ?? _voices.FirstOrDefault()?.Id;
            _preparedSpeech = null;

            if (_synthesizer is not null)
            {
                ApplySelectedVoice(_synthesizer, SelectedVoiceId);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task PrefetchAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text) || !OperatingSystem.IsWindows())
        {
            return;
        }

        string? selectedVoiceId;
        int selectedRate;
        CancellationTokenSource prefetchCts;

        cancellationToken.ThrowIfCancellationRequested();

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            selectedVoiceId = SelectedVoiceId;
            selectedRate = Rate;

            if (MatchesPreparedSpeech(selectedVoiceId, selectedRate, text))
            {
                return;
            }

            CancelPrefetchCore();
            prefetchCts = new CancellationTokenSource();
            _prefetchCts = prefetchCts;
        }
        finally
        {
            _mutex.Release();
        }

        try
        {
            using var synthesizer = CreateConfiguredSynthesizer(selectedVoiceId, selectedRate);
            using var stream = await synthesizer.SynthesizeTextToStreamAsync(text);
            var audioBytes = await ReadSpeechBytesAsync(stream, cancellationToken);

            await _mutex.WaitAsync(cancellationToken);
            try
            {
                if (prefetchCts.IsCancellationRequested || !ReferenceEquals(_prefetchCts, prefetchCts))
                {
                    return;
                }

                _preparedSpeech = new PreparedSpeech(selectedVoiceId, selectedRate, text, audioBytes);
                _prefetchCts = null;
            }
            finally
            {
                _mutex.Release();
            }
        }
        catch
        {
            await _mutex.WaitAsync(CancellationToken.None);
            try
            {
                if (ReferenceEquals(_prefetchCts, prefetchCts))
                {
                    _prefetchCts = null;
                }
            }
            finally
            {
                _mutex.Release();
            }
        }
        finally
        {
            prefetchCts.Dispose();
        }
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text) || !OperatingSystem.IsWindows())
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            CancelPrefetchCore();
            StopPlaybackCore();

            if (MatchesPreparedSpeech(SelectedVoiceId, Rate, text))
            {
                var preparedAudio = _preparedSpeech!.AudioBytes;
                _preparedSpeech = null;
                StartPlayback(preparedAudio);
                return;
            }

            var synthesizer = EnsureSynthesizer();
            ApplySelectedVoice(synthesizer, SelectedVoiceId);
            ApplyRate(synthesizer, Rate);

            using var stream = await synthesizer.SynthesizeTextToStreamAsync(text);
            var audioBytes = await ReadSpeechBytesAsync(stream, cancellationToken);
            StartPlayback(audioBytes);
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
            if (_waveOut is null || !IsSpeaking || IsPaused)
            {
                return;
            }

            _waveOut.Pause();
            IsSpeaking = false;
            IsPaused = true;
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
            if (_waveOut is null || !IsPaused)
            {
                return;
            }

            _waveOut.Play();
            IsSpeaking = true;
            IsPaused = false;
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
            CancelPrefetchCore();
            _preparedSpeech = null;
            StopPlaybackCore();
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
        CancelPrefetchCore();
        CancelPlaybackMonitor();
        StopPlaybackCore();
        _synthesizer?.Dispose();
        _synthesizer = null;
        _mutex.Dispose();
    }

    private SpeechSynthesizer EnsureSynthesizer()
    {
        _synthesizer ??= CreateConfiguredSynthesizer(SelectedVoiceId, Rate);
        return _synthesizer;
    }

    private static SpeechSynthesizer CreateConfiguredSynthesizer(string? selectedVoiceId, int rate)
    {
        var synthesizer = new SpeechSynthesizer();
        ApplySelectedVoice(synthesizer, selectedVoiceId);
        ApplyRate(synthesizer, rate);
        return synthesizer;
    }

    private static void ApplySelectedVoice(SpeechSynthesizer synthesizer, string? selectedVoiceId)
    {
        if (string.IsNullOrWhiteSpace(selectedVoiceId))
        {
            return;
        }

        var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, selectedVoiceId, StringComparison.OrdinalIgnoreCase));

        if (voice is not null)
        {
            synthesizer.Voice = voice;
        }
    }

    private static void ApplyRate(SpeechSynthesizer synthesizer, int rate)
    {
        synthesizer.Options.SpeakingRate = rate switch
        {
            <= -3 => 0.6,
            -2 => 0.75,
            -1 => 0.9,
            0 => 1.0,
            1 => 1.15,
            2 => 1.3,
            _ => 1.45
        };
    }

    private void StartPlayback(byte[] audioBytes)
    {
        _audioBuffer = new MemoryStream(audioBytes, writable: false);
        _waveReader = new WaveFileReader(_audioBuffer);
        var sampleProvider = new OffsetSampleProvider(_waveReader.ToSampleProvider())
        {
            DelayBy = LeadInDuration
        };

        _waveOut = new WaveOutEvent();
        _waveOut.Init(sampleProvider.ToWaveProvider());
        _waveOut.Play();

        _playbackMonitorCts = new CancellationTokenSource();
        _ = MonitorPlaybackAsync(_waveOut, _playbackMonitorCts.Token);
        IsSpeaking = true;
        IsPaused = false;
    }

    private async Task MonitorPlaybackAsync(WaveOutEvent waveOut, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (waveOut.PlaybackState == PlaybackState.Stopped)
                {
                    await _mutex.WaitAsync(cancellationToken);
                    try
                    {
                        if (ReferenceEquals(_waveOut, waveOut))
                        {
                            CleanupPlaybackResources();
                            IsSpeaking = false;
                            IsPaused = false;
                            PlaybackCompleted?.Invoke(this, EventArgs.Empty);
                        }
                    }
                    finally
                    {
                        _mutex.Release();
                    }

                    return;
                }

                await Task.Delay(50, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void StopPlaybackCore()
    {
        CancelPlaybackMonitor();

        try
        {
            _waveOut?.Stop();
        }
        catch
        {
        }

        CleanupPlaybackResources();
        IsSpeaking = false;
        IsPaused = false;
    }

    private void CleanupPlaybackResources()
    {
        _waveOut?.Dispose();
        _waveReader?.Dispose();
        _audioBuffer?.Dispose();
        _waveOut = null;
        _waveReader = null;
        _audioBuffer = null;
    }

    private void CancelPrefetchCore()
    {
        _prefetchCts?.Cancel();
        _prefetchCts?.Dispose();
        _prefetchCts = null;
    }

    private void CancelPlaybackMonitor()
    {
        _playbackMonitorCts?.Cancel();
        _playbackMonitorCts?.Dispose();
        _playbackMonitorCts = null;
    }

    private static async Task<byte[]> ReadSpeechBytesAsync(SpeechSynthesisStream stream, CancellationToken cancellationToken)
    {
        using var input = stream.GetInputStreamAt(0);
        using var reader = new DataReader(input);
        var size = checked((uint)stream.Size);
        await reader.LoadAsync(size).AsTask(cancellationToken);
        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static string BuildVoiceDisplayName(VoiceInformation voice)
    {
        return string.IsNullOrWhiteSpace(voice.Language)
            ? voice.DisplayName
            : $"{voice.DisplayName} ({voice.Language})";
    }

    private bool MatchesPreparedSpeech(string? voiceId, int rate, string text)
    {
        return _preparedSpeech is not null
            && string.Equals(_preparedSpeech.VoiceId, voiceId, StringComparison.OrdinalIgnoreCase)
            && _preparedSpeech.Rate == rate
            && string.Equals(_preparedSpeech.Text, text, StringComparison.Ordinal);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsSpeechService));
        }
    }

    private sealed record PreparedSpeech(string? VoiceId, int Rate, string Text, byte[] AudioBytes);
}
