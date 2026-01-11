using System.Windows.Controls;
using ProxyStarter.App.ViewModels;

namespace ProxyStarter.App.Views;

public partial class SettingsPage : Page
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
