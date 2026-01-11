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
    private System.Threading.Timer? _timer;

    public SubscriptionAutoUpdateService(SubscriptionStore subscriptionStore, SubscriptionService subscriptionService)
    {
        _subscriptionStore = subscriptionStore;
        _subscriptionService = subscriptionService;
    }

    public void Start()
    {
        if (_timer is not null)
        {
            return;
        }

        _timer = new System.Threading.Timer(OnTick, null, InitialDelay, TickInterval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose()
    {
        Stop();
        _gate.Dispose();
    }

    private async void OnTick(object? state)
    {
        if (!await _gate.WaitAsync(0))
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
                    await _subscriptionService.RefreshProfileAsync(profile);
                }
                catch
                {
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
