using System.Windows;
using Wpf.Ui.Controls;

namespace ProxyStarter.App.Views;

public partial class EditSubscriptionFileDialog : FluentWindow
{
    public EditSubscriptionFileDialog()
    {
        InitializeComponent();
    }

    public string ContentText => EditorBox.Text ?? string.Empty;

    public void SetContent(string profileName, string content)
    {
        var title = GetString("Text_EditSubscriptionFile", "Edit Subscription File");
        Title = title;

        HeaderText.Text = string.IsNullOrWhiteSpace(profileName)
            ? title
            : $"{title} - {profileName}";

        EditorBox.Text = content ?? string.Empty;
        EditorBox.CaretIndex = 0;
        EditorBox.ScrollToHome();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static string GetString(string key, string fallback)
    {
        return Application.Current?.TryFindResource(key) as string ?? fallback;
    }
}

