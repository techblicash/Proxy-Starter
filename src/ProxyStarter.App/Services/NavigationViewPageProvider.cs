using System;
using Wpf.Ui.Abstractions;

namespace ProxyStarter.App.Services;

public sealed class NavigationViewPageProvider : INavigationViewPageProvider
{
    private readonly IServiceProvider _serviceProvider;

    public NavigationViewPageProvider(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public object? GetPage(Type pageType)
    {
        return _serviceProvider.GetService(pageType);
    }
}
