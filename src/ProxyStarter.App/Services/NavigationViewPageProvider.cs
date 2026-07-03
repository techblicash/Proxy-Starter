using System;
using System.Collections.Generic;
using Wpf.Ui.Abstractions;

namespace ProxyStarter.App.Services;

public sealed class NavigationViewPageProvider : INavigationViewPageProvider
{
    private readonly IServiceProvider _serviceProvider;
    private readonly Dictionary<Type, object> _pageCache = new();

    public NavigationViewPageProvider(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public object? GetPage(Type pageType)
    {
        if (_pageCache.TryGetValue(pageType, out var cached))
        {
            return cached;
        }

        var page = _serviceProvider.GetService(pageType);
        if (page is null)
        {
            return null;
        }

        _pageCache[pageType] = page;
        return page;
    }
}
