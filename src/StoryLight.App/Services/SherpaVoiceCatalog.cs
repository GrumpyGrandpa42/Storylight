using System.Text.Json;
using SherpaOnnx;

namespace StoryLight.App.Services;

internal sealed class SherpaVoiceCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public SherpaVoiceCatalog()
    {
        VoicesRootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StoryLight",
            "voices",
            "sherpa-onnx");
    }

    public string VoicesRootDirectory { get; }

    public SherpaVoiceCatalogResult Discover()
    {
        Directory.CreateDirectory(VoicesRootDirectory);

        var voices = new List<SherpaVoiceDefinition>();
        var invalidFolders = 0;

        foreach (var folder in Directory.EnumerateDirectories(VoicesRootDirectory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (TryLoadVoice(folder, out var definition))
            {
                voices.Add(definition);
            }
            else
            {
                invalidFolders++;
            }
        }

        var statusSummary = voices.Count switch
        {
            > 0 when invalidFolders > 0 => $"Loaded {voices.Count} sherpa voice(s). Some voice folders were ignored.",
            > 0 => $"Loaded {voices.Count} sherpa voice(s) from {VoicesRootDirectory}.",
            _ when invalidFolders > 0 => $"No valid sherpa voices found in {VoicesRootDirectory}. Check storylight.voice.json and required model files.",
            _ => $"No sherpa voices found in {VoicesRootDirectory}. Use Open Voices Folder and add a storylight.voice.json manifest."
        };

        return new SherpaVoiceCatalogResult(voices, statusSummary);
    }

    private bool TryLoadVoice(string folder, out SherpaVoiceDefinition definition)
    {
        definition = default!;

        var manifestPath = Path.Combine(folder, "storylight.voice.json");
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<SherpaVoiceManifest>(json, JsonOptions);
            if (manifest is null)
            {
                return false;
            }

            var voiceId = string.IsNullOrWhiteSpace(manifest.Id)
                ? Path.GetFileName(folder)
                : manifest.Id.Trim();

            if (string.IsNullOrWhiteSpace(voiceId))
            {
                return false;
            }

            var displayName = string.IsNullOrWhiteSpace(manifest.DisplayName)
                ? voiceId
                : manifest.DisplayName.Trim();

            var config = BuildConfig(folder, manifest);
            var speakerId = Math.Max(0, manifest.SpeakerId);
            definition = new SherpaVoiceDefinition(voiceId, displayName, manifest.ModelType?.Trim().ToLowerInvariant() ?? string.Empty, folder, config, speakerId);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static OfflineTtsConfig BuildConfig(string folder, SherpaVoiceManifest manifest)
    {
        var modelType = manifest.ModelType?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(modelType))
        {
            throw new InvalidOperationException("ModelType is required.");
        }

        var modelConfig = new OfflineTtsModelConfig
        {
            NumThreads = manifest.NumThreads > 0 ? manifest.NumThreads : Math.Max(1, Math.Min(Environment.ProcessorCount, 4)),
            Debug = 0,
            Provider = string.IsNullOrWhiteSpace(manifest.Provider) ? "cpu" : manifest.Provider.Trim()
        };

        switch (modelType)
        {
            case "kokoro":
                modelConfig.Kokoro = new OfflineTtsKokoroModelConfig
                {
                    Model = ResolveRequiredFile(folder, manifest.Model, nameof(manifest.Model)),
                    Voices = ResolveRequiredFile(folder, manifest.Voices, nameof(manifest.Voices)),
                    Tokens = ResolveRequiredFile(folder, manifest.Tokens, nameof(manifest.Tokens)),
                    DataDir = ResolveRequiredDirectory(folder, manifest.DataDir, nameof(manifest.DataDir)),
                    DictDir = ResolveOptionalDirectory(folder, manifest.DictDir),
                    Lexicon = ResolveOptionalFile(folder, manifest.Lexicon),
                    Lang = string.IsNullOrWhiteSpace(manifest.Lang) ? "en-us" : manifest.Lang.Trim(),
                    LengthScale = manifest.LengthScale > 0 ? manifest.LengthScale : 1.0f
                };
                break;

            case "vits":
                modelConfig.Vits = new OfflineTtsVitsModelConfig
                {
                    Model = ResolveRequiredFile(folder, manifest.Model, nameof(manifest.Model)),
                    Tokens = ResolveRequiredFile(folder, manifest.Tokens, nameof(manifest.Tokens)),
                    Lexicon = ResolveOptionalFile(folder, manifest.Lexicon),
                    DataDir = ResolveOptionalDirectory(folder, manifest.DataDir),
                    DictDir = ResolveOptionalDirectory(folder, manifest.DictDir),
                    NoiseScale = manifest.NoiseScale > 0 ? manifest.NoiseScale : 0.667f,
                    NoiseScaleW = manifest.NoiseScaleW > 0 ? manifest.NoiseScaleW : 0.8f,
                    LengthScale = manifest.LengthScale > 0 ? manifest.LengthScale : 1.0f
                };
                break;

            default:
                throw new InvalidOperationException($"Unsupported model type: {modelType}");
        }

        return new OfflineTtsConfig
        {
            Model = modelConfig,
            MaxNumSentences = manifest.MaxNumSentences > 0 ? manifest.MaxNumSentences : 1,
            RuleFsts = string.Empty,
            RuleFars = string.Empty,
            SilenceScale = manifest.SilenceScale >= 0 ? manifest.SilenceScale : 0.2f
        };
    }

    private static string ResolveRequiredFile(string folder, string? path, string propertyName)
    {
        var resolvedPath = ResolvePath(folder, path);
        if (resolvedPath is null || !File.Exists(resolvedPath))
        {
            throw new FileNotFoundException($"Required file is missing for {propertyName}.", resolvedPath);
        }

        return resolvedPath;
    }

    private static string ResolveRequiredDirectory(string folder, string? path, string propertyName)
    {
        var resolvedPath = ResolvePath(folder, path);
        if (resolvedPath is null || !Directory.Exists(resolvedPath))
        {
            throw new DirectoryNotFoundException($"Required directory is missing for {propertyName}: {resolvedPath}");
        }

        return resolvedPath;
    }

    private static string ResolveOptionalFile(string folder, string? path)
    {
        var resolvedPath = ResolvePath(folder, path);
        return resolvedPath is not null && File.Exists(resolvedPath) ? resolvedPath : string.Empty;
    }

    private static string ResolveOptionalDirectory(string folder, string? path)
    {
        var resolvedPath = ResolvePath(folder, path);
        return resolvedPath is not null && Directory.Exists(resolvedPath) ? resolvedPath : string.Empty;
    }

    private static string? ResolvePath(string folder, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(folder, path));
    }
}

internal sealed record SherpaVoiceCatalogResult(IReadOnlyList<SherpaVoiceDefinition> Voices, string StatusSummary);

internal sealed record SherpaVoiceDefinition(
    string Id,
    string DisplayName,
    string ModelType,
    string FolderPath,
    OfflineTtsConfig Config,
    int SpeakerId);

internal sealed class SherpaVoiceManifest
{
    public string? Id { get; set; }
    public string? DisplayName { get; set; }
    public string? ModelType { get; set; }
    public string? Provider { get; set; }
    public int NumThreads { get; set; }
    public int MaxNumSentences { get; set; }
    public float SilenceScale { get; set; } = 0.2f;
    public int SpeakerId { get; set; }
    public string? Model { get; set; }
    public string? Voices { get; set; }
    public string? Tokens { get; set; }
    public string? DataDir { get; set; }
    public string? DictDir { get; set; }
    public string? Lexicon { get; set; }
    public string? Lang { get; set; }
    public float LengthScale { get; set; } = 1.0f;
    public float NoiseScale { get; set; } = 0.667f;
    public float NoiseScaleW { get; set; } = 0.8f;
}
