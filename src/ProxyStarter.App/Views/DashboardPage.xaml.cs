using System.Windows.Controls;
using ProxyStarter.App.ViewModels;

namespace ProxyStarter.App.Views;

public partial class DashboardPage : Page
{
    public DashboardPage(DashboardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
