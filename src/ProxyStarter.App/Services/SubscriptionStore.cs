using System.Collections.Generic;
using System;
using System.IO;
using System.Text.Json;
using ProxyStarter.App.Models;

namespace ProxyStarter.App.Services;

public sealed class SubscriptionStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    private readonly string _subscriptionsPath;

    public SubscriptionStore()
    {
        _subscriptionsPath = Path.Combine(AppPaths.DataDirectory, "subscriptions.json");
    }

    public IReadOnlyList<SubscriptionProfile> Load()
    {
        try
        {
            if (!File.Exists(_subscriptionsPath))
            {
                return new List<SubscriptionProfile>();
            }

            var json = File.ReadAllText(_subscriptionsPath);
            var profiles = JsonSerializer.Deserialize<List<SubscriptionProfile>>(json) ?? new List<SubscriptionProfile>();
            var updated = false;
            foreach (var profile in profiles)
            {
                if (string.IsNullOrWhiteSpace(profile.Id))
                {
                    profile.Id = Guid.NewGuid().ToString("N");
                    updated = true;
                }

                if (profile.AutoUpdateIntervalMinutes <= 0)
                {
                    profile.AutoUpdateIntervalMinutes = 360;
                    updated = true;
                }
            }

            if (updated)
            {
                Save(profiles);
            }

            return profiles;
        }
        catch
        {
            return new List<SubscriptionProfile>();
        }
    }

    public void Save(IEnumerable<SubscriptionProfile> profiles)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var json = JsonSerializer.Serialize(profiles, Options);
        File.WriteAllText(_subscriptionsPath, json);
    }
}
