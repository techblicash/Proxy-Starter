using System;
using System.Threading;
using System.Threading.Tasks;
using Velopack;

namespace ProxyStarter.App.Services;

public sealed class UpdateService
{
    private readonly AppSettingsStore _settingsStore;

    public UpdateService(AppSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public async Task CheckForUpdatesAsync(bool silent, CancellationToken cancellationToken = default)
    {
        var feedUrl = _settingsStore.Settings.UpdateFeedUrl;
        if (string.IsNullOrWhiteSpace(feedUrl))
        {
            return;
        }

        try
        {
            var manager = new UpdateManager(feedUrl);
            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
            {
                return;
            }

            await manager.DownloadUpdatesAsync(update, progress: null);
            if (!silent)
            {
                manager.ApplyUpdatesAndRestart(update, restartArgs: Array.Empty<string>());
            }
        }
        catch
        {
        }
    }
}
