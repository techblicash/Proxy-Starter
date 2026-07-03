using System.Windows.Controls;
using ProxyStarter.App.Helpers;
using ProxyStarter.App.ViewModels;

namespace ProxyStarter.App.Views;

public partial class LogsPage : Page
{
    public LogsPage(LogsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        Loaded += (_, _) => (DataContext as IPageLifecycleAware)?.OnPageActivated();
        Unloaded += (_, _) => (DataContext as IPageLifecycleAware)?.OnPageDeactivated();
    }
}
