using System;
using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace ProxyStarter.App.Services;

public sealed class LocalizationService
{
    private static bool s_languageMetadataOverridden;

    public event EventHandler? LanguageChanged;

    public void ApplyLanguage(string? language)
    {
        var culture = NormalizeLanguage(language);
        var cultureInfo = new CultureInfo(culture);
        var xmlLanguage = XmlLanguage.GetLanguage(cultureInfo.IetfLanguageTag);

        CultureInfo.DefaultThreadCurrentCulture = cultureInfo;
        CultureInfo.DefaultThreadCurrentUICulture = cultureInfo;
        if (!s_languageMetadataOverridden)
        {
            FrameworkElement.LanguageProperty.OverrideMetadata(
                typeof(FrameworkElement),
                new FrameworkPropertyMetadata(xmlLanguage));
            s_languageMetadataOverridden = true;
        }
        else if (Application.Current is not null)
        {
            foreach (Window window in Application.Current.Windows)
            {
                window.Language = xmlLanguage;
            }
        }

        UpdateResources(culture);
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public string GetString(string key, string fallback)
    {
        if (Application.Current?.Resources[key] is string value)
        {
            return value;
        }

        return fallback;
    }

    private static void UpdateResources(string culture)
    {
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"Resources/Strings.{culture}.xaml", UriKind.Relative)
        };

        var merged = resources.MergedDictionaries;
        for (var i = merged.Count - 1; i >= 0; i--)
        {
            var source = merged[i].Source?.OriginalString ?? string.Empty;
            if (source.IndexOf("Strings.", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                merged.RemoveAt(i);
            }
        }

        merged.Add(dictionary);
    }

    private static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return "en-US";
        }

        if (language.Contains("中文", StringComparison.OrdinalIgnoreCase)
            || language.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return "zh-CN";
        }

        return "en-US";
    }
}
