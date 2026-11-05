using System.Windows;

namespace DocPivot.App.Services;

public enum AppTheme
{
    Light,
    Dark,
}

/// <summary>
/// Swaps the colour dictionary merged at the top of the application resources.
/// Every themed brush is referenced dynamically, so open windows repaint in place.
/// </summary>
public static class ThemeService
{
    private const string LightSource = "Themes/Light.xaml";
    private const string DarkSource = "Themes/Dark.xaml";

    public static AppTheme Current { get; private set; } = AppTheme.Light;

    public static bool IsDark => Current == AppTheme.Dark;

    public static event EventHandler? ThemeChanged;

    /// <summary>Reads a stored preference; the first release stored "Paper" / "Graphite".</summary>
    public static AppTheme Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "dark" or "graphite" => AppTheme.Dark,
        _ => AppTheme.Light,
    };

    public static void Apply(AppTheme theme)
    {
        var application = Application.Current;
        if (application is null)
        {
            Current = theme;
            return;
        }

        var dictionaries = application.Resources.MergedDictionaries;
        var index = -1;
        for (var i = 0; i < dictionaries.Count; i++)
        {
            var source = dictionaries[i].Source?.OriginalString;
            if (source is not null &&
                (source.EndsWith(LightSource, StringComparison.OrdinalIgnoreCase) ||
                 source.EndsWith(DarkSource, StringComparison.OrdinalIgnoreCase)))
            {
                index = i;
                break;
            }
        }

        var wanted = theme == AppTheme.Dark ? DarkSource : LightSource;
        if (index >= 0 &&
            dictionaries[index].Source!.OriginalString.EndsWith(wanted, StringComparison.OrdinalIgnoreCase))
        {
            Current = theme;
            return;
        }

        var replacement = new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/{typeof(ThemeService).Assembly.GetName().Name};component/{wanted}",
                UriKind.Absolute),
        };
        if (index >= 0)
        {
            dictionaries[index] = replacement;
        }
        else
        {
            dictionaries.Insert(0, replacement);
        }

        Current = theme;
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }
}
