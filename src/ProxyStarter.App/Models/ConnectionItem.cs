using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ProxyStarter.App.Models;

public sealed partial class ConnectionItem : ObservableObject
{
    [ObservableProperty]
    private string _id = string.Empty;

    [ObservableProperty]
    private string _target = string.Empty;

    [ObservableProperty]
    private string _network = string.Empty;

    [ObservableProperty]
    private string _type = string.Empty;

    [ObservableProperty]
    private string _rule = string.Empty;

    [ObservableProperty]
    private string _proxy = string.Empty;

    [ObservableProperty]
    private string _process = string.Empty;

    [ObservableProperty]
    private string _age = string.Empty;

    [ObservableProperty]
    private long _upload;

    [ObservableProperty]
    private long _download;

    [ObservableProperty]
    private DateTimeOffset _start;
}
