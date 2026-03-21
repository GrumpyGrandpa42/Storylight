using StoryLight.App.Models;

namespace StoryLight.App.Services;

public sealed class HybridTextToSpeechService : ITextToSpeechService
{
    public event EventHandler? PlaybackCompleted;

    private const string SherpaPrefix = "sherpa:";
    private const string WindowsPrefix = "windows:";

    private readonly SherpaOnnxSpeechService _sherpaService = new();
    private readonly WindowsSpeechService _windowsService = new();
    private readonly Dictionary<string, VoiceSelection> _voiceMap = new(StringComparer.OrdinalIgnoreCase);
    private ITextToSpeechService? _speakingService;
    private int _rate;

    public HybridTextToSpeechService()
    {
        _sherpaService.PlaybackCompleted += OnInnerPlaybackCompleted;
        _windowsService.PlaybackCompleted += OnInnerPlaybackCompleted;
    }

    public bool IsAvailable => Voices.Count > 0;
    public bool IsSpeaking => (_speakingService ?? GetSelectedService())?.IsSpeaking == true;
    public bool IsPaused => (_speakingService ?? GetSelectedService())?.IsPaused == true;

    public int Rate
    {
        get => _rate;
        set
        {
            _rate = value;
            _sherpaService.Rate = value;
            _windowsService.Rate = value;
        }
    }

    public string? SelectedVoiceId { get; private set; }
    public string VoicesFolderPath => _sherpaService.VoicesFolderPath;
    public string StatusSummary { get; private set; } = "No voices available.";
    public IReadOnlyList<TtsVoiceInfo> Voices { get; private set; } = Array.Empty<TtsVoiceInfo>();

    public async Task InitializeAsync(SpeechSettings settings, CancellationToken cancellationToken = default)
    {
        Rate = settings.Rate;
        await _sherpaService.InitializeAsync(settings, cancellationToken);
        await _windowsService.InitializeAsync(settings, cancellationToken);
        await RefreshAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(settings.VoiceId))
        {
            await SetVoiceAsync(settings.VoiceId, cancellationToken);
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _sherpaService.RefreshAsync(cancellationToken);
        await _windowsService.RefreshAsync(cancellationToken);

        _voiceMap.Clear();
        var voices = new List<TtsVoiceInfo>();

        foreach (var voice in _sherpaService.Voices)
        {
            var id = SherpaPrefix + voice.Id;
            voices.Add(new TtsVoiceInfo(id, $"Sherpa · {voice.DisplayName}"));
            _voiceMap[id] = new VoiceSelection(_sherpaService, voice.Id);
        }

        foreach (var voice in _windowsService.Voices)
        {
            var id = WindowsPrefix + voice.Id;
            voices.Add(new TtsVoiceInfo(id, $"Windows · {voice.DisplayName}"));
            _voiceMap[id] = new VoiceSelection(_windowsService, voice.Id);
        }

        Voices = voices;

        if (Voices.Count == 0)
        {
            SelectedVoiceId = null;
            StatusSummary = $"{_sherpaService.StatusSummary} {_windowsService.StatusSummary}".Trim();
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedVoiceId) || !_voiceMap.ContainsKey(SelectedVoiceId))
        {
            SelectedVoiceId = Voices[0].Id;
        }

        StatusSummary = BuildStatusSummary();
    }

    public async Task SetVoiceAsync(string? voiceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(voiceId) || !_voiceMap.TryGetValue(voiceId, out var selection))
        {
            SelectedVoiceId = Voices.FirstOrDefault()?.Id;
            if (SelectedVoiceId is not null && _voiceMap.TryGetValue(SelectedVoiceId, out selection))
            {
                await selection.Service.SetVoiceAsync(selection.InnerVoiceId, cancellationToken);
            }

            return;
        }

        SelectedVoiceId = voiceId;
        await selection.Service.SetVoiceAsync(selection.InnerVoiceId, cancellationToken);
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        var selection = GetSelectedSelection();
        if (selection is null)
        {
            return;
        }

        await selection.Value.Service.SetVoiceAsync(selection.Value.InnerVoiceId, cancellationToken);
        _speakingService = selection.Value.Service;
        await selection.Value.Service.SpeakAsync(text, cancellationToken);
    }

    public async Task PauseAsync()
    {
        var service = _speakingService ?? GetSelectedService();
        if (service is not null)
        {
            await service.PauseAsync();
        }
    }

    public async Task ResumeAsync()
    {
        var service = _speakingService ?? GetSelectedService();
        if (service is not null)
        {
            await service.ResumeAsync();
        }
    }

    public async Task StopAsync()
    {
        var service = _speakingService ?? GetSelectedService();
        _speakingService = null;
        if (service is not null)
        {
            await service.StopAsync();
        }
    }

    public void Dispose()
    {
        _sherpaService.PlaybackCompleted -= OnInnerPlaybackCompleted;
        _windowsService.PlaybackCompleted -= OnInnerPlaybackCompleted;
        _sherpaService.Dispose();
        _windowsService.Dispose();
    }

    private VoiceSelection? GetSelectedSelection()
    {
        return SelectedVoiceId is not null && _voiceMap.TryGetValue(SelectedVoiceId, out var selection)
            ? selection
            : null;
    }

    private ITextToSpeechService? GetSelectedService()
    {
        return GetSelectedSelection()?.Service;
    }

    private string BuildStatusSummary()
    {
        if (_sherpaService.IsAvailable)
        {
            return _sherpaService.StatusSummary;
        }

        if (_windowsService.IsAvailable)
        {
            return "Using built-in Windows voices. Add sherpa voices in Voices Folder for offline model voices.";
        }

        return $"{_sherpaService.StatusSummary} {_windowsService.StatusSummary}".Trim();
    }

    private void OnInnerPlaybackCompleted(object? sender, EventArgs e)
    {
        _speakingService = null;
        PlaybackCompleted?.Invoke(this, EventArgs.Empty);
    }

    private readonly record struct VoiceSelection(ITextToSpeechService Service, string InnerVoiceId);
}
