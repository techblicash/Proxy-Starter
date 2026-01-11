using System.Windows.Controls;
using ProxyStarter.App.ViewModels;

namespace ProxyStarter.App.Views;

public partial class RulesPage : Page
{
    public RulesPage(RulesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
