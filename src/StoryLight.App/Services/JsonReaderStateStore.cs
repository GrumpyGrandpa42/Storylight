using System.Text.Json;
using StoryLight.App.Models;

namespace StoryLight.App.Services;

public sealed class JsonReaderStateStore : IReaderStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _statePath;

    public JsonReaderStateStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StoryLight");

        Directory.CreateDirectory(root);
        _statePath = Path.Combine(root, "state.json");
    }

    public async Task<AppState> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_statePath))
        {
            return new AppState();
        }

        await using var stream = File.OpenRead(_statePath);
        var state = await JsonSerializer.DeserializeAsync<AppState>(stream, JsonOptions, cancellationToken);
        return state ?? new AppState();
    }

    public async Task SaveAsync(AppState state, CancellationToken cancellationToken = default)
    {
        await using var stream = File.Create(_statePath);
        await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
    }
}
