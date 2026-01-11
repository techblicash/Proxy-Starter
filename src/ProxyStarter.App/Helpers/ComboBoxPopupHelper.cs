using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using WpfComboBox = System.Windows.Controls.ComboBox;

namespace ProxyStarter.App.Helpers;

public static class ComboBoxPopupHelper
{
    public static readonly DependencyProperty UseCrispPopupProperty =
        DependencyProperty.RegisterAttached(
            "UseCrispPopup",
            typeof(bool),
            typeof(ComboBoxPopupHelper),
            new PropertyMetadata(false, OnUseCrispPopupChanged));

    public static bool GetUseCrispPopup(DependencyObject obj) =>
        (bool)obj.GetValue(UseCrispPopupProperty);

    public static void SetUseCrispPopup(DependencyObject obj, bool value) =>
        obj.SetValue(UseCrispPopupProperty, value);

    private static void OnUseCrispPopupChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WpfComboBox combo)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            combo.Loaded += OnComboLoaded;
            combo.DropDownOpened += OnComboDropDownOpened;
        }
        else
        {
            combo.Loaded -= OnComboLoaded;
            combo.DropDownOpened -= OnComboDropDownOpened;
        }
    }

    private static void OnComboLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is WpfComboBox combo)
        {
            ApplyPopupSettings(combo);
        }
    }

    private static void OnComboDropDownOpened(object? sender, EventArgs e)
    {
        if (sender is WpfComboBox combo)
        {
            ApplyPopupSettings(combo);
        }
    }

    private static void ApplyPopupSettings(WpfComboBox combo)
    {
        if (combo.Template.FindName("PART_Popup", combo) is not Popup popup)
        {
            return;
        }

        popup.AllowsTransparency = false;
        popup.SnapsToDevicePixels = true;
        popup.UseLayoutRounding = true;

        if (popup.Child is FrameworkElement child)
        {
            child.SnapsToDevicePixels = true;
            child.UseLayoutRounding = true;
            RenderOptions.SetClearTypeHint(child, ClearTypeHint.Enabled);
            TextOptions.SetTextFormattingMode(child, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(child, TextRenderingMode.ClearType);
            TextOptions.SetTextHintingMode(child, TextHintingMode.Fixed);
        }
    }
}
