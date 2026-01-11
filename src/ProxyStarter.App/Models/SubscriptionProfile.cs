using System;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ProxyStarter.App.Models;

public sealed partial class SubscriptionProfile : ObservableObject
{
    public string Id { get; set; } = string.Empty;

    [ObservableProperty]
    private string _name = "New Subscription";

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    private DateTimeOffset? _lastUpdated;

    [ObservableProperty]
    private int _nodeCount;

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private bool _autoUpdateEnabled = true;

    [ObservableProperty]
    private int _autoUpdateIntervalMinutes = 360;

    [ObservableProperty]
    [JsonIgnore]
    private bool _isActive;
}

