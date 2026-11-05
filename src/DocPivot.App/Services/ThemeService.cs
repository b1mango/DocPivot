using System.Windows;

namespace DocPivot.App.Services;

public enum AppTheme
{
    Paper,
    Graphite,
}

/// <summary>
/// Swaps the colour dictionary merged at the top of the application resources.
/// Every themed brush is referenced dynamically, so open windows repaint in place.
/// </summary>
public static class ThemeService
{
    private const string PaperSource = "Themes/Paper.xaml";
    private const string GraphiteSource = "Themes/Graphite.xaml";

    public static AppTheme Current { get; private set; } = AppTheme.Paper;

    public static bool IsDark => Current == AppTheme.Graphite;

    public static event EventHandler? ThemeChanged;

    public static AppTheme Parse(string? value) =>
        Enum.TryParse<AppTheme>(value, ignoreCase: true, out var theme) ? theme : AppTheme.Paper;

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
                (source.EndsWith(PaperSource, StringComparison.OrdinalIgnoreCase) ||
                 source.EndsWith(GraphiteSource, StringComparison.OrdinalIgnoreCase)))
            {
                index = i;
                break;
            }
        }

        var wanted = theme == AppTheme.Graphite ? GraphiteSource : PaperSource;
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
