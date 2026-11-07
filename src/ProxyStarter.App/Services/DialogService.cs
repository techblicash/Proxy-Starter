using System.Threading.Tasks;
using System;
using System.Windows;
using ProxyStarter.App.Models;
using ProxyStarter.App.Views;
using Wpf.Ui.Controls;
using WpfMessageBox = Wpf.Ui.Controls.MessageBox;

namespace ProxyStarter.App.Services;

public enum DialogError
{
    CoreNotFound,
    CoreOperation,
    NetworkSettings,
    Elevation,
    SubscriptionDownload,
    NoSubscription,
    NoSubscriptionCache,
    SubscriptionSave,
    ModeSave,
    ModeUpdate,
    RulesSave,
    NodeSelection,
    QosApply,
    RestartRequired,
    SettingsSave,
    UpdateFeedMissing,
    UpdateUnavailable,
    UpdateCheck,
    CoreDownload,
}

public interface IDialogService
{
    Task ShowInfoAsync(string title, string message);
    Task ShowErrorAsync(DialogError error, string? details = null);
    Task<bool> ShowConfirmAsync(string title, string message);
    SubscriptionProfile? ShowAddSubscriptionDialog();
    SubscriptionProfile? ShowEditSubscriptionDialog(SubscriptionProfile existing);
    string? ShowEditSubscriptionFileDialog(string profileName, string content);
}

public sealed class DialogService : IDialogService
{
    private readonly LocalizationService _localization;

    public DialogService(LocalizationService localization)
    {
        _localization = localization;
    }

    public async Task ShowInfoAsync(string title, string message)
    {
        await ShowMessageAsync(title, message, _localization.GetString("DialogOk", "OK"));
    }

    public async Task ShowErrorAsync(DialogError error, string? details = null)
    {
        var title = _localization.GetString($"Error_{error}_Title", "Error");
        var message = _localization.GetString($"Error_{error}_Body", string.Empty);
        if (!string.IsNullOrWhiteSpace(details))
        {
            var label = _localization.GetString("DialogDetails", "Technical details");
            message += $"\n\n{label}\n{details}";
        }

        await ShowMessageAsync(title, message, _localization.GetString("DialogClose", "Close"));
    }

    public async Task<bool> ShowConfirmAsync(string title, string message)
    {
        var dialog = BuildMessageBox(title, message,
            _localization.GetString("DialogYes", "Yes"), _localization.GetString("DialogNo", "No"));
        var result = await dialog.ShowDialogAsync(true);
        return result == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }

    public SubscriptionProfile? ShowAddSubscriptionDialog()
    {
        var dialog = new AddSubscriptionDialog
        {
            Owner = Application.Current?.MainWindow
        };

        var result = dialog.ShowDialog();
        if (result != true)
        {
            return null;
        }

        return new SubscriptionProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = dialog.ProfileName,
            Url = dialog.ProfileUrl,
            IsEnabled = true,
            AutoUpdateEnabled = dialog.AutoUpdateEnabled,
            AutoUpdateIntervalMinutes = dialog.AutoUpdateIntervalMinutes
        };
    }

    public SubscriptionProfile? ShowEditSubscriptionDialog(SubscriptionProfile existing)
    {
        if (existing is null)
        {
            return null;
        }

        var dialog = new AddSubscriptionDialog
        {
            Owner = Application.Current?.MainWindow
        };

        dialog.SetProfile(existing.Name, existing.Url, existing.AutoUpdateEnabled, existing.AutoUpdateIntervalMinutes, isEditMode: true);

        var result = dialog.ShowDialog();
        if (result != true)
        {
            return null;
        }

        var name = dialog.ProfileName;
        if (string.IsNullOrWhiteSpace(name))
        {
            if (Uri.TryCreate(dialog.ProfileUrl, UriKind.Absolute, out var uri))
            {
                name = uri.Host;
            }
        }

        return new SubscriptionProfile
        {
            Id = existing.Id,
            Name = name,
            Url = dialog.ProfileUrl,
            IsEnabled = existing.IsEnabled,
            AutoUpdateEnabled = dialog.AutoUpdateEnabled,
            AutoUpdateIntervalMinutes = dialog.AutoUpdateIntervalMinutes,
            NodeCount = existing.NodeCount,
            LastUpdated = existing.LastUpdated
        };
    }

    public string? ShowEditSubscriptionFileDialog(string profileName, string content)
    {
        var dialog = new EditSubscriptionFileDialog
        {
            Owner = Application.Current?.MainWindow
        };

        dialog.SetContent(profileName, content);

        var result = dialog.ShowDialog();
        if (result != true)
        {
            return null;
        }

        return dialog.ContentText;
    }

    private async Task ShowMessageAsync(string title, string message, string closeText)
    {
        var dialog = BuildMessageBox(title, message, closeText, null);
        await dialog.ShowDialogAsync(true);
    }

    private static WpfMessageBox BuildMessageBox(string title, string message, string primaryText, string? secondaryText)
    {
        var hasSecondary = !string.IsNullOrWhiteSpace(secondaryText);
        var dialog = new WpfMessageBox
        {
            Title = title,
            Content = new System.Windows.Controls.ScrollViewer
            {
                MaxHeight = 360,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 560
                }
            },
            PrimaryButtonText = primaryText,
            SecondaryButtonText = secondaryText ?? string.Empty,
            CloseButtonText = string.Empty,
            IsSecondaryButtonEnabled = hasSecondary,
            IsCloseButtonEnabled = false,
            ShowTitle = true,
            Owner = Application.Current?.MainWindow
        };

        return dialog;
    }
}
