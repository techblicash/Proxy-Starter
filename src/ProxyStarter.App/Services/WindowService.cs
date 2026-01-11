using System.Windows;

namespace ProxyStarter.App.Services;

public interface IWindowService
{
    void ShowMainWindow();
    void HideMainWindow();
}

public sealed class WindowService : IWindowService
{
    public void ShowMainWindow()
    {
        if (Application.Current?.MainWindow is not Window window)
        {
            return;
        }

        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    public void HideMainWindow()
    {
        Application.Current?.MainWindow?.Hide();
    }
}
