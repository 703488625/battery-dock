using System.Windows;
using BatteryDock.Models;
using Microsoft.Win32;

namespace BatteryDock.Services;

internal static class ThemeManager
{
    public static bool IsSystemDark()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                1);
            return value is int number && number == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsDark(AppThemeMode mode) => mode switch
    {
        AppThemeMode.Dark => true,
        AppThemeMode.Light => false,
        _ => IsSystemDark()
    };

    public static void Apply(AppThemeMode mode)
    {
        var application = System.Windows.Application.Current;
        if (application == null) return;

        var isDark = IsDark(mode);
        var source = new Uri(
            $"pack://application:,,,/BatteryDock;component/Themes/{(isDark ? "Dark" : "Light")}.xaml",
            UriKind.Absolute);

        var dictionaries = application.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source != null &&
            dictionary.Source.OriginalString.Contains("/Themes/", StringComparison.OrdinalIgnoreCase) &&
            (dictionary.Source.OriginalString.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase) ||
             dictionary.Source.OriginalString.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase)));

        var replacement = new ResourceDictionary { Source = source };

        // Remove every previously applied theme dictionary. Looking only at
        // Source can miss compiled BAML entries, so identify them by a key that
        // is unique to the theme dictionaries.
        for (var index = dictionaries.Count - 1; index >= 0; index--)
        {
            var dictionary = dictionaries[index];
            var sourceText = dictionary.Source?.OriginalString ?? string.Empty;
            if (dictionary.Contains("WindowBackgroundBrush") ||
                sourceText.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase) ||
                sourceText.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase))
            {
                dictionaries.RemoveAt(index);
            }
        }

        // The newest merged dictionary wins resource lookup. Adding at the end
        // also makes repeated theme switches deterministic.
        dictionaries.Add(replacement);
        Logger.Info($"主题：{mode} -> {(isDark ? "Dark" : "Light")}");

        try
        {
            if (application.Resources["WindowBackgroundBrush"] is System.Windows.Media.SolidColorBrush brush)
            {
                Logger.Info($"主题背景色：{brush.Color}");
            }
        }
        catch { }
    }
}
