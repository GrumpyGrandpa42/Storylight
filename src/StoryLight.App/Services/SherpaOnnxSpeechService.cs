using NAudio.Wave;
using SherpaOnnx;
using StoryLight.App.Models;

namespace StoryLight.App.Services;

public sealed class SherpaOnnxSpeechService : ITextToSpeechService
{
    public event EventHandler? PlaybackCompleted;

    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly SherpaVoiceCatalog _voiceCatalog = new();
    private readonly Dictionary<string, OfflineTts> _engineCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SherpaVoiceDefinition> _voiceDefinitions = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _generationCts;
    private CancellationTokenSource? _playbackMonitorCts;
    private WaveOutEvent? _waveOut;
    private RawSourceWaveStream? _audioStream;
    private MemoryStream? _audioBuffer;
    private bool _disposed;
    private int _rate;
    private IReadOnlyList<TtsVoiceInfo> _voices = Array.Empty<TtsVoiceInfo>();

    public bool IsAvailable => _voices.Count > 0;
    public bool IsSpeaking { get; private set; }
    public bool IsPaused { get; private set; }

    public int Rate
    {
        get => _rate;
        set => _rate = Math.Clamp(value, -3, 3);
    }

    public string? SelectedVoiceId { get; private set; }
    public string VoicesFolderPath => _voiceCatalog.VoicesRootDirectory;
    public string StatusSummary { get; private set; } = "No sherpa voices loaded.";
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

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();

            var discovery = _voiceCatalog.Discover();
            var currentVoiceIds = discovery.Voices.Select(voice => voice.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            DisposeMissingEngines(currentVoiceIds);

            _voiceDefinitions.Clear();
            foreach (var definition in discovery.Voices)
            {
                _voiceDefinitions[definition.Id] = definition;
            }

            _voices = discovery.Voices
                .Select(definition => new TtsVoiceInfo(definition.Id, definition.DisplayName))
                .ToArray();

            if (_voices.Count == 0)
            {
                SelectedVoiceId = null;
                CancelGenerationCore();
                StopPlaybackCore();
            }
            else if (string.IsNullOrWhiteSpace(SelectedVoiceId) || !_voiceDefinitions.ContainsKey(SelectedVoiceId))
            {
                SelectedVoiceId = _voices[0].Id;
            }

            StatusSummary = discovery.StatusSummary;
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
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        SherpaVoiceDefinition definition;
        OfflineTts engine;
        CancellationTokenSource generationCts;

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();

            if (SelectedVoiceId is null || !_voiceDefinitions.TryGetValue(SelectedVoiceId, out definition!))
            {
                throw new InvalidOperationException(StatusSummary);
            }

            CancelGenerationCore();
            StopPlaybackCore();

            engine = GetOrCreateEngineCore(definition);
            generationCts = new CancellationTokenSource();
            _generationCts = generationCts;
            IsSpeaking = true;
            IsPaused = false;
        }
        finally
        {
            _mutex.Release();
        }

        OfflineTtsGeneratedAudio? generatedAudio = null;
        try
        {
            var speed = MapRateToSpeed(Rate);
            generatedAudio = await Task.Run(() => engine.Generate(text, speed, definition.SpeakerId), CancellationToken.None);

            await _mutex.WaitAsync(cancellationToken);
            try
            {
                if (generationCts.IsCancellationRequested || !ReferenceEquals(_generationCts, generationCts))
                {
                    return;
                }

                StartPlaybackCore(generatedAudio);
                generatedAudio = null;
            }
            finally
            {
                _mutex.Release();
            }
        }
        catch
        {
            await _mutex.WaitAsync(cancellationToken);
            try
            {
                if (ReferenceEquals(_generationCts, generationCts))
                {
                    _generationCts = null;
                }

                IsSpeaking = false;
                IsPaused = false;
            }
            finally
            {
                _mutex.Release();
            }

            throw;
        }
        finally
        {
            generatedAudio?.Dispose();
            generationCts.Dispose();
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
            CancelGenerationCore();
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

        CancelGenerationCore();
        StopPlaybackCore();

        foreach (var engine in _engineCache.Values)
        {
            engine.Dispose();
        }

        _engineCache.Clear();
        _mutex.Dispose();
    }

    private OfflineTts GetOrCreateEngineCore(SherpaVoiceDefinition definition)
    {
        if (_engineCache.TryGetValue(definition.Id, out var existing))
        {
            return existing;
        }

        var engine = new OfflineTts(definition.Config);
        _engineCache[definition.Id] = engine;
        return engine;
    }

    private void DisposeMissingEngines(HashSet<string> availableVoiceIds)
    {
        foreach (var staleVoiceId in _engineCache.Keys.Where(id => !availableVoiceIds.Contains(id)).ToArray())
        {
            _engineCache[staleVoiceId].Dispose();
            _engineCache.Remove(staleVoiceId);
        }
    }

    private void StartPlaybackCore(OfflineTtsGeneratedAudio generatedAudio)
    {
        var audioBytes = ConvertSamplesToBytes(generatedAudio.Samples);
        _audioBuffer = new MemoryStream(audioBytes, writable: false);
        _audioStream = new RawSourceWaveStream(_audioBuffer, WaveFormat.CreateIeeeFloatWaveFormat(generatedAudio.SampleRate, 1));
        _waveOut = new WaveOutEvent();
        _waveOut.Init(_audioStream);
        _waveOut.Play();

        _generationCts = null;
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

                await Task.Delay(150, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CancelGenerationCore()
    {
        _generationCts?.Cancel();
        _generationCts = null;
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
        _audioStream?.Dispose();
        _audioBuffer?.Dispose();
        _waveOut = null;
        _audioStream = null;
        _audioBuffer = null;
    }

    private void CancelPlaybackMonitor()
    {
        _playbackMonitorCts?.Cancel();
        _playbackMonitorCts?.Dispose();
        _playbackMonitorCts = null;
    }

    private static byte[] ConvertSamplesToBytes(float[] samples)
    {
        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float MapRateToSpeed(int rate)
    {
        return rate switch
        {
            <= -3 => 0.7f,
            -2 => 0.82f,
            -1 => 0.92f,
            0 => 1.0f,
            1 => 1.08f,
            2 => 1.18f,
            _ => 1.28f
        };
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SherpaOnnxSpeechService));
        }
    }
}
