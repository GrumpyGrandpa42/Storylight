using NAudio.Wave;
using SherpaOnnx;
using StoryLight.App.Models;

namespace StoryLight.App.Services;

public sealed class SherpaOnnxSpeechService : ITextToSpeechService
{
    private static readonly TimeSpan InitialLeadInDuration = TimeSpan.FromMilliseconds(120);

    public event EventHandler? PlaybackCompleted;

    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly SherpaVoiceCatalog _voiceCatalog = new();
    private readonly Dictionary<string, OfflineTts> _engineCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SherpaVoiceDefinition> _voiceDefinitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PreparedSpeech> _preparedSpeechCache = new();
    private readonly Queue<SpeechRequestKey> _playbackSequence = new();
    private readonly List<PrefetchRequest> _prefetchQueue = new();
    private CancellationTokenSource? _generationCts;
    private CancellationTokenSource? _prefetchWorkerCts;
    private Task? _activeGenerationTask;
    private Task? _prefetchWorkerTask;
    private PrefetchRequest? _activePrefetchRequest;
    private WaveOutEvent? _waveOut;
    private QueuedWaveProvider? _queuedWaveProvider;
    private WaveFormat? _playbackFormat;
    private bool _disposed;
    private int _rate;
    private IReadOnlyList<TtsVoiceInfo> _voices = Array.Empty<TtsVoiceInfo>();

    public bool IsAvailable => _voices.Count > 0;
    public bool IsSpeaking => _playbackSequence.Count > 0 && !IsPaused;
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
                CancelPrefetchCore(clearPreparedSpeech: true);
                StopPlaybackCore();
            }
            else if (string.IsNullOrWhiteSpace(SelectedVoiceId) || !_voiceDefinitions.ContainsKey(SelectedVoiceId))
            {
                SelectedVoiceId = _voices[0].Id;
                ClearPreparedSpeechCacheCore();
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
            CancelPrefetchCore(clearPreparedSpeech: true);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task PrefetchAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        PrefetchRequest request;
        Task completionTask;

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();

            if (SelectedVoiceId is null || !_voiceDefinitions.TryGetValue(SelectedVoiceId, out var definition))
            {
                return;
            }

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
                request = new PrefetchRequest(
                    requestKey,
                    definition,
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
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        SherpaVoiceDefinition definition;
        OfflineTts engine;
        CancellationTokenSource generationCts;
        int selectedRate;

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

            if (TryTakePreparedSpeechCore(new SpeechRequestKey(SelectedVoiceId, Rate, text), out var preparedSpeech))
            {
                StartPlaybackCore(preparedSpeech, includeLeadIn: true);
                return;
            }

            CancelPrefetchCore(clearPreparedSpeech: false);
            engine = GetOrCreateEngineCore(definition);
            selectedRate = Rate;
            generationCts = new CancellationTokenSource();
            _generationCts = generationCts;
            IsPaused = false;
        }
        finally
        {
            _mutex.Release();
        }

        OfflineTtsGeneratedAudio? generatedAudio = null;
        Task<OfflineTtsGeneratedAudio>? generationTask = null;
        try
        {
            var speed = MapRateToSpeed(selectedRate);
            generationTask = Task.Run(() => engine.Generate(text, speed, definition.SpeakerId), CancellationToken.None);
            await _mutex.WaitAsync(cancellationToken);
            try
            {
                _activeGenerationTask = generationTask;
            }
            finally
            {
                _mutex.Release();
            }

            generatedAudio = await generationTask;
            var audioBytes = ConvertSamplesToBytes(generatedAudio.Samples);

            await _mutex.WaitAsync(cancellationToken);
            try
            {
                if (generationCts.IsCancellationRequested || !ReferenceEquals(_generationCts, generationCts))
                {
                    return;
                }

                StartPlaybackCore(
                    new PreparedSpeech(
                        SelectedVoiceId,
                        selectedRate,
                        text,
                        audioBytes,
                        WaveFormat.CreateIeeeFloatWaveFormat(generatedAudio.SampleRate, 1)),
                    includeLeadIn: true);
                generatedAudio = null;
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
                if (ReferenceEquals(_generationCts, generationCts))
                {
                    _generationCts = null;
                }

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
            await _mutex.WaitAsync(CancellationToken.None);
            try
            {
                if (ReferenceEquals(_activeGenerationTask, generationTask))
                {
                    _activeGenerationTask = null;
                }
            }
            finally
            {
                _mutex.Release();
            }

            generatedAudio?.Dispose();
            generationCts.Dispose();
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
            CancelGenerationCore();
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

        CancelGenerationCore();
        CancelPrefetchCore(clearPreparedSpeech: true);
        StopPlaybackCore();
        CleanupPlaybackResources();
        WaitForBackgroundWork();

        foreach (var engine in _engineCache.Values)
        {
            engine.Dispose();
        }

        _engineCache.Clear();
        _mutex.Dispose();
    }

    private void WaitForBackgroundWork()
    {
        Task?[] tasks;

        try
        {
            _mutex.Wait();
            tasks = new[] { _activeGenerationTask, _prefetchWorkerTask };
        }
        finally
        {
            if (_mutex.CurrentCount == 0)
            {
                _mutex.Release();
            }
        }

        var pendingTasks = tasks.Where(task => task is not null).Cast<Task>().ToArray();
        if (pendingTasks.Length == 0)
        {
            return;
        }

        try
        {
            Task.WhenAll(pendingTasks).GetAwaiter().GetResult();
        }
        catch
        {
        }
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

    private void CancelGenerationCore()
    {
        _generationCts?.Cancel();
        _generationCts = null;
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
            OfflineTts engine;

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

                engine = GetOrCreateEngineCore(request.Definition);
            }
            finally
            {
                _mutex.Release();
            }

            OfflineTtsGeneratedAudio? generatedAudio = null;
            try
            {
                var speed = MapRateToSpeed(request.Key.Rate);
                generatedAudio = await Task.Run(
                    () => engine.Generate(request.Key.Text, speed, request.Definition.SpeakerId),
                    CancellationToken.None);

                var audioBytes = ConvertSamplesToBytes(generatedAudio.Samples);

                await _mutex.WaitAsync(CancellationToken.None);
                try
                {
                    if (cancellationToken.IsCancellationRequested || _disposed)
                    {
                        request.Completion.TrySetCanceled();
                        return;
                    }

                    StorePreparedSpeechCore(new PreparedSpeech(
                        request.Key.VoiceId,
                        request.Key.Rate,
                        request.Key.Text,
                        audioBytes,
                        WaveFormat.CreateIeeeFloatWaveFormat(generatedAudio.SampleRate, 1)));
                    TryQueuePreparedSpeechCore(_preparedSpeechCache[^1]);
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
            finally
            {
                generatedAudio?.Dispose();
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

    private void StartPlaybackCore(PreparedSpeech preparedSpeech, bool includeLeadIn)
    {
        var format = preparedSpeech.AudioFormat;
        var needsLeadIn = includeLeadIn && InitializePlaybackCore(format);
        _queuedWaveProvider!.Clear();
        _playbackSequence.Clear();

        var initialAudio = needsLeadIn
            ? CombineAudioBytes(CreateSilenceBytes(format, InitialLeadInDuration), preparedSpeech.AudioBytes)
            : preparedSpeech.AudioBytes;

        _queuedWaveProvider.Enqueue(initialAudio);
        _playbackSequence.Enqueue(preparedSpeech.Key);
        _waveOut!.Play();

        _generationCts = null;
        IsPaused = false;
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

    private static byte[] ConvertSamplesToBytes(float[] samples)
    {
        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static byte[] CreateSilenceBytes(WaveFormat format, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return Array.Empty<byte>();
        }

        var bytesPerSecond = format.AverageBytesPerSecond;
        var silenceLength = (int)Math.Round(bytesPerSecond * duration.TotalSeconds);
        return silenceLength <= 0 ? Array.Empty<byte>() : new byte[silenceLength];
    }

    private static byte[] CombineAudioBytes(byte[] prefix, byte[] content)
    {
        if (prefix.Length == 0)
        {
            return content;
        }

        var combined = new byte[prefix.Length + content.Length];
        Buffer.BlockCopy(prefix, 0, combined, 0, prefix.Length);
        Buffer.BlockCopy(content, 0, combined, prefix.Length, content.Length);
        return combined;
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
            throw new ObjectDisposedException(nameof(SherpaOnnxSpeechService));
        }
    }

    private sealed record SpeechRequestKey(string VoiceId, int Rate, string Text);

    private sealed record PreparedSpeech(string VoiceId, int Rate, string Text, byte[] AudioBytes, WaveFormat AudioFormat)
    {
        public SpeechRequestKey Key => new(VoiceId, Rate, Text);
    }

    private sealed record PrefetchRequest(
        SpeechRequestKey Key,
        SherpaVoiceDefinition Definition,
        TaskCompletionSource Completion);
}
