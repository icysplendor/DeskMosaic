using WinAssistant.Audio.Models;
using WinAssistant.Settings;

namespace WinAssistant.Audio.Storage;

public sealed class UnifiedAudioConfigStore : IConfigStore
{
    public Task<AppConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ConfigurationStore.Shared.Configuration.Audio);
    }

    public Task SaveAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        ConfigurationStore.Shared.Update(configuration => configuration.Audio = config);
        return Task.CompletedTask;
    }
}
