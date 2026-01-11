using System.Windows.Controls;
using ProxyStarter.App.ViewModels;

namespace ProxyStarter.App.Views;

public partial class ConnectionsPage : Page
{
    public ConnectionsPage(ConnectionsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
