using NAudio.Wave;
using StoryLight.App.Models;
using Windows.Media.SpeechSynthesis;
using Windows.Storage.Streams;

namespace StoryLight.App.Services;

public sealed class WindowsSpeechService : ITextToSpeechService
{
    private static readonly TimeSpan InitialLeadInDuration = TimeSpan.FromMilliseconds(120);

    public event EventHandler? PlaybackCompleted;

    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly List<PreparedSpeech> _preparedSpeechCache = new();
    private readonly Queue<SpeechRequestKey> _playbackSequence = new();
    private readonly List<PrefetchRequest> _prefetchQueue = new();
    private SpeechSynthesizer? _synthesizer;
    private CancellationTokenSource? _prefetchWorkerCts;
    private Task? _prefetchWorkerTask;
    private PrefetchRequest? _activePrefetchRequest;
    private WaveOutEvent? _waveOut;
    private QueuedWaveProvider? _queuedWaveProvider;
    private WaveFormat? _playbackFormat;
    private bool _disposed;
    private int _rate;
    private IReadOnlyList<TtsVoiceInfo> _voices = Array.Empty<TtsVoiceInfo>();

    public bool IsAvailable => OperatingSystem.IsWindows() && _voices.Count > 0;
    public bool IsSpeaking => _playbackSequence.Count > 0 && !IsPaused;
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
                CancelPrefetchCore(clearPreparedSpeech: true);
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
            CancelPrefetchCore(clearPreparedSpeech: true);

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

        Task completionTask;

        cancellationToken.ThrowIfCancellationRequested();

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var requestKey = new SpeechRequestKey(SelectedVoiceId, Rate, text);

            if (ContainsPreparedSpeechCore(requestKey))
            {
                return;
            }

            if (TryGetPendingPrefetchCore(requestKey, out var existingRequest))
            {
                completionTask = existingRequest.Completion.Task;
            }
            else
            {
                var request = new PrefetchRequest(
                    requestKey,
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                _prefetchQueue.Add(request);
                completionTask = request.Completion.Task;
                EnsurePrefetchWorkerCore();
            }
        }
        finally
        {
            _mutex.Release();
        }

        await completionTask.WaitAsync(cancellationToken);
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
            StopPlaybackCore();
            InitializePlaybackCore(AudioPlaybackUtilities.PlaybackFormat);

            if (TryTakePreparedSpeechCore(new SpeechRequestKey(SelectedVoiceId, Rate, text), out var preparedSpeech))
            {
                StartPlaybackCore(preparedSpeech, includeLeadIn: true);
                return;
            }

            CancelPrefetchCore(clearPreparedSpeech: false);
            var synthesizer = EnsureSynthesizer();
            ApplySelectedVoice(synthesizer, SelectedVoiceId);
            ApplyRate(synthesizer, Rate);

            using var stream = await synthesizer.SynthesizeTextToStreamAsync(text);
            var generatedSpeech = await CreatePreparedSpeechAsync(SelectedVoiceId, Rate, text, stream, cancellationToken);
            StartPlaybackCore(generatedSpeech, includeLeadIn: true);
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
            if (_waveOut is null || _playbackSequence.Count == 0 || IsPaused)
            {
                return;
            }

            _waveOut.Pause();
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
            if (_waveOut is null || _playbackSequence.Count == 0 || !IsPaused)
            {
                return;
            }

            _waveOut.Play();
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
            CancelPrefetchCore(clearPreparedSpeech: true);
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
        CancelPrefetchCore(clearPreparedSpeech: true);
        StopPlaybackCore();
        CleanupPlaybackResources();
        try
        {
            _prefetchWorkerTask?.GetAwaiter().GetResult();
        }
        catch
        {
        }

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

    private void StartPlaybackCore(PreparedSpeech preparedSpeech, bool includeLeadIn)
    {
        var needsLeadIn = includeLeadIn && InitializePlaybackCore(preparedSpeech.AudioFormat);
        _queuedWaveProvider!.Clear();
        _playbackSequence.Clear();

        var initialAudio = needsLeadIn
            ? CombineAudioBytes(CreateSilenceBytes(preparedSpeech.AudioFormat, InitialLeadInDuration), preparedSpeech.AudioBytes)
            : preparedSpeech.AudioBytes;

        _queuedWaveProvider.Enqueue(initialAudio);
        _playbackSequence.Enqueue(preparedSpeech.Key);
        _waveOut!.Play();
        IsPaused = false;
    }

    private void StopPlaybackCore()
    {
        _queuedWaveProvider?.Clear();
        _playbackSequence.Clear();

        if (_waveOut?.PlaybackState == PlaybackState.Paused)
        {
            _waveOut.Play();
        }

        IsPaused = false;
    }

    private void CleanupPlaybackResources()
    {
        if (_queuedWaveProvider is not null)
        {
            _queuedWaveProvider.SegmentCompleted -= OnPlaybackSegmentCompleted;
        }

        if (_waveOut is not null)
        {
            _waveOut.PlaybackStopped -= OnWaveOutPlaybackStopped;
        }

        _waveOut?.Dispose();
        _waveOut = null;
        _queuedWaveProvider = null;
        _playbackFormat = null;
        _playbackSequence.Clear();
    }

    private void CancelPrefetchCore(bool clearPreparedSpeech)
    {
        _prefetchWorkerCts?.Cancel();
        _prefetchWorkerCts?.Dispose();
        _prefetchWorkerCts = null;

        foreach (var queuedRequest in _prefetchQueue)
        {
            queuedRequest.Completion.TrySetCanceled();
        }

        _prefetchQueue.Clear();
        _activePrefetchRequest?.Completion.TrySetCanceled();
        _activePrefetchRequest = null;

        if (clearPreparedSpeech)
        {
            ClearPreparedSpeechCacheCore();
        }
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

    private void EnsurePrefetchWorkerCore()
    {
        if (_prefetchWorkerTask is { IsCompleted: false })
        {
            return;
        }

        _prefetchWorkerCts?.Dispose();
        _prefetchWorkerCts = new CancellationTokenSource();
        _prefetchWorkerTask = ProcessPrefetchQueueAsync(_prefetchWorkerCts.Token);
    }

    private async Task ProcessPrefetchQueueAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            PrefetchRequest request;

            await _mutex.WaitAsync(CancellationToken.None);
            try
            {
                if (cancellationToken.IsCancellationRequested || _disposed)
                {
                    return;
                }

                if (_prefetchQueue.Count == 0)
                {
                    _activePrefetchRequest = null;
                    return;
                }

                request = _prefetchQueue[0];
                _prefetchQueue.RemoveAt(0);
                _activePrefetchRequest = request;

                if (ContainsPreparedSpeechCore(request.Key))
                {
                    request.Completion.TrySetResult();
                    _activePrefetchRequest = null;
                    continue;
                }
            }
            finally
            {
                _mutex.Release();
            }

            try
            {
                using var synthesizer = CreateConfiguredSynthesizer(request.Key.VoiceId, request.Key.Rate);
                using var stream = await synthesizer.SynthesizeTextToStreamAsync(request.Key.Text);
                var preparedSpeech = await CreatePreparedSpeechAsync(
                    request.Key.VoiceId,
                    request.Key.Rate,
                    request.Key.Text,
                    stream,
                    CancellationToken.None);

                await _mutex.WaitAsync(CancellationToken.None);
                try
                {
                    if (cancellationToken.IsCancellationRequested || _disposed)
                    {
                        request.Completion.TrySetCanceled();
                        return;
                    }

                    StorePreparedSpeechCore(preparedSpeech);
                    TryQueuePreparedSpeechCore(preparedSpeech);
                    request.Completion.TrySetResult();
                    _activePrefetchRequest = null;
                }
                finally
                {
                    _mutex.Release();
                }
            }
            catch (Exception ex)
            {
                await _mutex.WaitAsync(CancellationToken.None);
                try
                {
                    request.Completion.TrySetException(ex);
                    _activePrefetchRequest = null;
                }
                finally
                {
                    _mutex.Release();
                }
            }
        }
    }

    private bool TryGetPendingPrefetchCore(SpeechRequestKey key, out PrefetchRequest request)
    {
        if (_activePrefetchRequest is not null && _activePrefetchRequest.Key == key)
        {
            request = _activePrefetchRequest;
            return true;
        }

        var existingRequest = _prefetchQueue.FirstOrDefault(candidate => candidate.Key == key);
        if (existingRequest is not null)
        {
            request = existingRequest;
            return true;
        }

        request = null!;
        return false;
    }

    private bool ContainsPreparedSpeechCore(SpeechRequestKey key)
    {
        return _preparedSpeechCache.Any(candidate => candidate.Key == key);
    }

    private bool TryTakePreparedSpeechCore(SpeechRequestKey key, out PreparedSpeech preparedSpeech)
    {
        var index = _preparedSpeechCache.FindIndex(candidate => candidate.Key == key);
        if (index >= 0)
        {
            preparedSpeech = _preparedSpeechCache[index];
            _preparedSpeechCache.RemoveAt(index);
            return true;
        }

        preparedSpeech = null!;
        return false;
    }

    private void StorePreparedSpeechCore(PreparedSpeech preparedSpeech)
    {
        _preparedSpeechCache.RemoveAll(candidate => candidate.Key == preparedSpeech.Key);
        _preparedSpeechCache.Add(preparedSpeech);

        while (_preparedSpeechCache.Count > 2)
        {
            _preparedSpeechCache.RemoveAt(0);
        }
    }

    private void ClearPreparedSpeechCacheCore()
    {
        _preparedSpeechCache.Clear();
    }

    private bool TryQueuePreparedSpeechCore(PreparedSpeech preparedSpeech)
    {
        if (_waveOut is null || _queuedWaveProvider is null || _playbackFormat is null)
        {
            return false;
        }

        if (!WaveFormatMatches(_playbackFormat, preparedSpeech.AudioFormat))
        {
            return false;
        }

        if (_playbackSequence.Contains(preparedSpeech.Key))
        {
            return true;
        }

        _queuedWaveProvider.Enqueue(preparedSpeech.AudioBytes);
        _playbackSequence.Enqueue(preparedSpeech.Key);
        return true;
    }

    private bool InitializePlaybackCore(WaveFormat format)
    {
        if (_waveOut is not null && _queuedWaveProvider is not null && _playbackFormat is not null)
        {
            if (WaveFormatMatches(_playbackFormat, format))
            {
                if (_waveOut.PlaybackState != PlaybackState.Playing)
                {
                    _waveOut.Play();
                }

                return false;
            }

            CleanupPlaybackResources();
        }

        _playbackFormat = format;
        _queuedWaveProvider = new QueuedWaveProvider(format);
        _queuedWaveProvider.SegmentCompleted += OnPlaybackSegmentCompleted;

        _waveOut = new WaveOutEvent
        {
            DesiredLatency = 100,
            NumberOfBuffers = 2
        };
        _waveOut.PlaybackStopped += OnWaveOutPlaybackStopped;
        _waveOut.Init(_queuedWaveProvider);
        _waveOut.Play();
        return true;
    }

    private void OnPlaybackSegmentCompleted()
    {
        _ = Task.Run(async () =>
        {
            await _mutex.WaitAsync();
            try
            {
                if (_playbackSequence.Count == 0)
                {
                    return;
                }

                _playbackSequence.Dequeue();
                if (_playbackSequence.Count == 0 && _waveOut?.PlaybackState != PlaybackState.Paused)
                {
                    IsPaused = false;
                }
            }
            finally
            {
                _mutex.Release();
            }

            PlaybackCompleted?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnWaveOutPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        _ = Task.Run(async () =>
        {
            await _mutex.WaitAsync();
            try
            {
                if (!ReferenceEquals(_waveOut, sender))
                {
                    return;
                }

                CleanupPlaybackResources();
                IsPaused = false;
            }
            finally
            {
                _mutex.Release();
            }
        });
    }

    private static async Task<PreparedSpeech> CreatePreparedSpeechAsync(
        string? voiceId,
        int rate,
        string text,
        SpeechSynthesisStream stream,
        CancellationToken cancellationToken)
    {
        var waveBytes = await ReadSpeechBytesAsync(stream, cancellationToken);
        using var memoryStream = new MemoryStream(waveBytes, writable: false);
        using var waveReader = new WaveFileReader(memoryStream);
        using var pcmBuffer = new MemoryStream();
        waveReader.CopyTo(pcmBuffer);
        var convertedBytes = AudioPlaybackUtilities.ConvertToPlaybackFormat(pcmBuffer.ToArray(), waveReader.WaveFormat);
        return new PreparedSpeech(voiceId, rate, text, convertedBytes, AudioPlaybackUtilities.PlaybackFormat);
    }

    private static byte[] CreateSilenceBytes(WaveFormat format, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return Array.Empty<byte>();
        }

        var silenceLength = (int)Math.Round(format.AverageBytesPerSecond * duration.TotalSeconds);
        return silenceLength <= 0 ? Array.Empty<byte>() : new byte[silenceLength];
    }

    private static byte[] CombineAudioBytes(byte[] prefix, byte[] content)
    {
        if (prefix.Length == 0)
        {
            return content;
        }

        var combined = new byte[prefix.Length + content.Length];
        System.Buffer.BlockCopy(prefix, 0, combined, 0, prefix.Length);
        System.Buffer.BlockCopy(content, 0, combined, prefix.Length, content.Length);
        return combined;
    }

    private static bool WaveFormatMatches(WaveFormat left, WaveFormat right)
    {
        return left.Encoding == right.Encoding
            && left.SampleRate == right.SampleRate
            && left.Channels == right.Channels
            && left.BitsPerSample == right.BitsPerSample
            && left.BlockAlign == right.BlockAlign
            && left.AverageBytesPerSecond == right.AverageBytesPerSecond;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsSpeechService));
        }
    }

    private sealed record SpeechRequestKey(string? VoiceId, int Rate, string Text);

    private sealed record PreparedSpeech(string? VoiceId, int Rate, string Text, byte[] AudioBytes, WaveFormat AudioFormat)
    {
        public SpeechRequestKey Key => new(VoiceId, Rate, Text);
    }

    private sealed record PrefetchRequest(SpeechRequestKey Key, TaskCompletionSource Completion);
}
