using System.Windows.Controls;
using ProxyStarter.App.Helpers;
using ProxyStarter.App.ViewModels;

namespace ProxyStarter.App.Views;

public partial class DashboardPage : Page
{
    public DashboardPage(DashboardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        Loaded += (_, _) => (DataContext as IPageLifecycleAware)?.OnPageActivated();
        Unloaded += (_, _) => (DataContext as IPageLifecycleAware)?.OnPageDeactivated();
    }
}
