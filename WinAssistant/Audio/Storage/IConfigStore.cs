using WinAssistant.Audio.Models;

namespace WinAssistant.Audio.Storage;

public interface IConfigStore
{
    Task<AppConfig> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppConfig config, CancellationToken cancellationToken = default);
}
