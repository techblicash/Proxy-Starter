using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace ProxyStarter.App.Helpers;

public sealed class ObservableRangeCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotifications;

    public void ReplaceRange(IEnumerable<T> items)
    {
        if (items is null)
        {
            throw new ArgumentNullException(nameof(items));
        }

        CheckReentrancy();

        _suppressNotifications = true;
        try
        {
            Clear();
            foreach (var item in items)
            {
                Add(item);
            }
        }
        finally
        {
            _suppressNotifications = false;
        }

        RaiseReset();
    }

    public void AddRange(IEnumerable<T> items)
    {
        if (items is null)
        {
            throw new ArgumentNullException(nameof(items));
        }

        CheckReentrancy();

        _suppressNotifications = true;
        try
        {
            foreach (var item in items)
            {
                Add(item);
            }
        }
        finally
        {
            _suppressNotifications = false;
        }

        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (_suppressNotifications)
        {
            return;
        }

        base.OnPropertyChanged(e);
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_suppressNotifications)
        {
            return;
        }

        base.OnCollectionChanged(e);
    }
}
