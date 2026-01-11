using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ProxyStarter.App.ViewModels;

namespace ProxyStarter.App.Views;

public partial class ProfilesPage : Page
{
    public ProfilesPage(ProfilesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnRowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
    {
        if (DataContext is ProfilesViewModel viewModel)
        {
            viewModel.SaveProfilesCommand.Execute(null);
        }
    }

    private void OnGridMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is null)
        {
            return;
        }

        if (DataContext is ProfilesViewModel viewModel && viewModel.EditSelectedCommand.CanExecute(null))
        {
            viewModel.EditSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null && current is not T)
        {
            current = VisualTreeHelper.GetParent(current);
        }

        return current as T;
    }
}

