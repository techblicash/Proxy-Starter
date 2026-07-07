using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace ProxyStarter.App.Services;

public sealed class SoftwareUpdateService
{
    private static readonly IFileDownloader Downloader = new FallbackFileDownloader();

    private UpdateManager? _preparedManager;
    private VelopackAsset? _preparedAsset;

    public string CurrentVersion
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetName().Version
                          ?? Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "unknown" : version.ToString(3);
        }
    }

    public async Task<SoftwareUpdateCheckResult> CheckAndPrepareUpdateAsync(
        string updateFeedUrl,
        Action<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ClearPreparedUpdate();

        if (string.IsNullOrWhiteSpace(updateFeedUrl))
        {
            return new SoftwareUpdateCheckResult(
                SoftwareUpdateStatus.FeedNotConfigured,
                CurrentVersion,
                null,
                "Update feed URL is not configured.");
        }

        var manager = CreateUpdateManager(updateFeedUrl.Trim());
        var currentVersion = manager.CurrentVersion?.ToString() ?? CurrentVersion;

        if (!manager.IsInstalled)
        {
            return new SoftwareUpdateCheckResult(
                SoftwareUpdateStatus.NotInstalled,
                currentVersion,
                null,
                "The app is not running from a Velopack installation. Update checks are available after packaging/installing the app.");
        }

        if (manager.UpdatePendingRestart is not null)
        {
            _preparedManager = manager;
            _preparedAsset = manager.UpdatePendingRestart;
            return new SoftwareUpdateCheckResult(
                SoftwareUpdateStatus.PendingRestart,
                currentVersion,
                manager.UpdatePendingRestart.Version?.ToString(),
                "An update is already downloaded and ready to apply.");
        }

        var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (update is null)
        {
            return new SoftwareUpdateCheckResult(
                SoftwareUpdateStatus.NoUpdate,
                currentVersion,
                null,
                "You are already on the latest version.");
        }

        await manager.DownloadUpdatesAsync(update, progress, cancellationToken).ConfigureAwait(false);

        _preparedManager = manager;
        _preparedAsset = update.TargetFullRelease;

        return new SoftwareUpdateCheckResult(
            SoftwareUpdateStatus.UpdateReady,
            currentVersion,
            update.TargetFullRelease.Version?.ToString(),
            "Update downloaded and ready to apply.");
    }

    public void ApplyPreparedUpdateAndRestart()
    {
        if (_preparedManager is null)
        {
            throw new InvalidOperationException("No prepared update is available.");
        }

        _preparedManager.ApplyUpdatesAndRestart(_preparedAsset);
    }

    private void ClearPreparedUpdate()
    {
        _preparedManager = null;
        _preparedAsset = null;
    }

    private static UpdateManager CreateUpdateManager(string updateFeedUrl)
    {
        if (Uri.TryCreate(updateFeedUrl, UriKind.Absolute, out var uri)
            && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return new UpdateManager(new GithubSource(updateFeedUrl, string.Empty, prerelease: false, Downloader));
        }

        if (Uri.TryCreate(updateFeedUrl, UriKind.Absolute, out _))
        {
            return new UpdateManager(new SimpleWebSource(updateFeedUrl, Downloader));
        }

        return new UpdateManager(updateFeedUrl);
    }
}
