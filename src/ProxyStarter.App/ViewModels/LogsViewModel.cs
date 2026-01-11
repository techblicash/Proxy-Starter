using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyStarter.App.Models;
using ProxyStarter.App.Services;

namespace ProxyStarter.App.ViewModels;

public partial class LogsViewModel : ObservableObject
{
    private readonly MihomoLogService _logService;

    public ObservableCollection<LogEntry> Entries { get; } = new();

    public LogsViewModel(MihomoLogService logService)
    {
        _logService = logService;
        _logService.LogReceived += OnLogReceived;
        _logService.Start();
    }

    [RelayCommand]
    private void Clear()
    {
        Entries.Clear();
    }

    private void OnLogReceived(object? sender, LogEntry entry)
    {
        if (Application.Current is null)
        {
            return;
        }

        try
        {
            Application.Current.Dispatcher.BeginInvoke(() => Entries.Insert(0, entry));
        }
        catch
        {
        }
    }
}
