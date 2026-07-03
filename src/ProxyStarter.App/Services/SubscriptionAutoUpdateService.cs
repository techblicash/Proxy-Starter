using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProxyStarter.App.Models;

namespace ProxyStarter.App.Services;

public sealed class SubscriptionAutoUpdateService : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);

    private readonly SubscriptionStore _subscriptionStore;
    private readonly SubscriptionService _subscriptionService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public SubscriptionAutoUpdateService(SubscriptionStore subscriptionStore, SubscriptionService subscriptionService)
    {
        _subscriptionStore = subscriptionStore;
        _subscriptionService = subscriptionService;
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_cts is not null)
            {
                return;
            }

            var cts = new CancellationTokenSource();
            _cts = cts;
            _runTask = RunAsync(cts.Token).ContinueWith(
                _ => cts.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _cts?.Cancel();
            _cts = null;
        }
    }

    public void Dispose()
    {
        Stop();
        _gate.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(InitialDelay, cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested)
            {
                await OnTickAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(TickInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            CrashLogger.Log(ex, "SubscriptionAutoUpdateService: Run");
        }
    }

    private async Task OnTickAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var profiles = _subscriptionStore.Load().ToList();
            var dueProfiles = GetDueProfiles(profiles);
            if (dueProfiles.Count == 0)
            {
                return;
            }

            foreach (var profile in dueProfiles)
            {
                try
                {
                    await _subscriptionService.RefreshProfileAsync(profile, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    CrashLogger.Log(ex, $"SubscriptionAutoUpdateService: Refresh {profile.Name}");
                }
            }

            _subscriptionService.RebuildCatalogFromCache(profiles);
            _subscriptionStore.Save(profiles);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static List<SubscriptionProfile> GetDueProfiles(IEnumerable<SubscriptionProfile> profiles)
    {
        var now = DateTimeOffset.Now;
        var due = new List<SubscriptionProfile>();

        foreach (var profile in profiles)
        {
            if (!profile.IsEnabled || !profile.AutoUpdateEnabled)
            {
                continue;
            }

            var interval = profile.AutoUpdateIntervalMinutes;
            if (interval <= 0)
            {
                continue;
            }

            if (!profile.LastUpdated.HasValue)
            {
                due.Add(profile);
                continue;
            }

            var elapsed = now - profile.LastUpdated.Value;
            if (elapsed >= TimeSpan.FromMinutes(interval))
            {
                due.Add(profile);
            }
        }

        return due;
    }
}
