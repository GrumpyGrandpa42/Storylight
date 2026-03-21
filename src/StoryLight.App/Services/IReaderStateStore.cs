using StoryLight.App.Models;

namespace StoryLight.App.Services;

public interface IReaderStateStore
{
    Task<AppState> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppState state, CancellationToken cancellationToken = default);
}
