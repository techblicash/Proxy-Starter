using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProxyStarter.App.Helpers;
using ProxyStarter.App.Models;
using ProxyStarter.App.Services;

namespace ProxyStarter.App.ViewModels;

public partial class LogsViewModel : ObservableObject, IPageLifecycleAware
{
    private readonly MihomoLogService _logService;
    private readonly DispatcherTimer _flushTimer;
    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private readonly HashSet<LogEntry> _knownEntries = new();
    private bool _isActive;
    private const int MaxFlushPerTick = 80;

    public ObservableCollection<LogEntry> Entries { get; } = new();

    public LogsViewModel(MihomoLogService logService)
    {
        _logService = logService;
        _flushTimer = new DispatcherTimer
        {
            Interval = System.TimeSpan.FromMilliseconds(200)
        };
        _flushTimer.Tick += (_, _) => FlushPending();
    }

    [RelayCommand]
    private void Clear()
    {
        _logService.Clear();
        Entries.Clear();
        _knownEntries.Clear();
        while (_pending.TryDequeue(out _))
        {
        }
    }

    private void OnLogReceived(object? sender, LogEntry entry)
    {
        if (!_isActive)
        {
            return;
        }

        _pending.Enqueue(entry);
    }

    private void FlushPending()
    {
        if (!_isActive || Application.Current is null)
        {
            return;
        }

        var drained = new List<LogEntry>(MaxFlushPerTick);
        while (drained.Count < MaxFlushPerTick && _pending.TryDequeue(out var entry))
        {
            drained.Add(entry);
        }

        // Insert newest entries at the top; batch flushing reduces UI-thread pressure.
        foreach (var entry in drained)
        {
            if (!_knownEntries.Add(entry))
            {
                continue;
            }

            Entries.Insert(0, entry);
        }

        TrimEntriesToLimit();
    }

    public void OnPageActivated()
    {
        if (_isActive)
        {
            return;
        }

        _isActive = true;

        try
        {
            _logService.LogReceived -= OnLogReceived;
            _logService.LogReceived += OnLogReceived;
            _logService.Start();
        }
        catch
        {
        }

        LoadSnapshot();
        _flushTimer.Start();
        FlushPending();
    }

    public void OnPageDeactivated()
    {
        _isActive = false;
        _flushTimer.Stop();

        try
        {
            _logService.LogReceived -= OnLogReceived;
        }
        catch
        {
        }
    }

    private void LoadSnapshot()
    {
        var snapshot = _logService.GetSnapshot();
        Entries.Clear();
        _knownEntries.Clear();

        for (var i = snapshot.Count - 1; i >= 0; i--)
        {
            var entry = snapshot[i];
            if (_knownEntries.Add(entry))
            {
                Entries.Add(entry);
            }
        }

        TrimEntriesToLimit();
    }

    private void TrimEntriesToLimit()
    {
        var maxEntries = _logService.RetentionLimit;
        while (Entries.Count > maxEntries)
        {
            var lastIndex = Entries.Count - 1;
            var removed = Entries[lastIndex];
            Entries.RemoveAt(lastIndex);
            _knownEntries.Remove(removed);
        }
    }
}
