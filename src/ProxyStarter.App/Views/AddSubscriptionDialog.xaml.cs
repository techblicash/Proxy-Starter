using System;
using System.Globalization;
using System.Windows;
using Wpf.Ui.Controls;

namespace ProxyStarter.App.Views;

public partial class AddSubscriptionDialog : FluentWindow
{
    public AddSubscriptionDialog()
    {
        InitializeComponent();
        UpdateMaxHeightForWorkArea();
        Loaded += (_, _) => UpdateMaxHeightForWorkArea();
    }

    public string ProfileName => NameBox.Text.Trim();
    public string ProfileUrl => UrlBox.Text.Trim();
    public bool AutoUpdateEnabled => AutoUpdateBox.IsChecked == true;
    public int AutoUpdateIntervalMinutes => ParseInterval(AutoUpdateIntervalBox.Text);

    public void SetProfile(
        string? name,
        string? url,
        bool autoUpdateEnabled,
        int autoUpdateIntervalMinutes,
        bool isEditMode)
    {
        NameBox.Text = name ?? string.Empty;
        UrlBox.Text = url ?? string.Empty;
        AutoUpdateBox.IsChecked = autoUpdateEnabled;
        AutoUpdateIntervalBox.Text = autoUpdateIntervalMinutes.ToString(CultureInfo.InvariantCulture);

        if (!isEditMode)
        {
            return;
        }

        var editTitle = GetString("Text_EditSubscription", "Edit Subscription");
        Title = editTitle;
        HeaderText.Text = editTitle;
        ConfirmButton.Content = GetString("Text_Save", "Save");
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ProfileUrl))
        {
            return;
        }

        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static int ParseInterval(string? text)
    {
        if (!int.TryParse(text, out var value))
        {
            return 360;
        }

        if (value < 5)
        {
            return 5;
        }

        if (value > 10080)
        {
            return 10080;
        }

        return value;
    }

    private static string GetString(string key, string fallback)
    {
        return Application.Current?.TryFindResource(key) as string ?? fallback;
    }

    private void UpdateMaxHeightForWorkArea()
    {
        try
        {
            var max = SystemParameters.WorkArea.Height - 120;
            if (double.IsFinite(max) && max > 0)
            {
                MaxHeight = Math.Max(MinHeight, max);
            }
        }
        catch
        {
        }
    }
}
