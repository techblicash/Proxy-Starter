using System.Windows.Controls;
using ProxyStarter.App.ViewModels;

namespace ProxyStarter.App.Views;

public partial class LogsPage : Page
{
    public LogsPage(LogsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
