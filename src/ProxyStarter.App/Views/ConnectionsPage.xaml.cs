using System.Windows.Controls;
using ProxyStarter.App.Helpers;
using ProxyStarter.App.ViewModels;

namespace ProxyStarter.App.Views;

public partial class ConnectionsPage : Page
{
    public ConnectionsPage(ConnectionsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        Loaded += (_, _) => (DataContext as IPageLifecycleAware)?.OnPageActivated();
        Unloaded += (_, _) => (DataContext as IPageLifecycleAware)?.OnPageDeactivated();
    }
}
