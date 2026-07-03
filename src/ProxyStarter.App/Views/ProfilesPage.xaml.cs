using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ProxyStarter.App.Helpers;
using ProxyStarter.App.ViewModels;

namespace ProxyStarter.App.Views;

public partial class ProfilesPage : Page
{
    public ProfilesPage(ProfilesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        Loaded += (_, _) => (DataContext as IPageLifecycleAware)?.OnPageActivated();
        Unloaded += (_, _) => (DataContext as IPageLifecycleAware)?.OnPageDeactivated();
    }

    private void OnRowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
    {
        if (DataContext is ProfilesViewModel viewModel)
        {
            Dispatcher.BeginInvoke(() => viewModel.SaveProfilesCommand.Execute(null), System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void OnGridMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var source = e.OriginalSource as DependencyObject;
        if (FindAncestor<System.Windows.Controls.Primitives.ToggleButton>(source) is not null)
        {
            return;
        }

        if (FindAncestor<DataGridRow>(source) is null)
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
