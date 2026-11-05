using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;

namespace DocPivot.App.Services;

[SupportedOSPlatform("windows")]
public static class WindowBackdropService
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmWindowCornerPreferenceRound = 2;

    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        TrySetDwmAttribute(handle, DwmUseImmersiveDarkMode, ThemeService.IsDark ? 1 : 0);
        TrySetDwmAttribute(handle, DwmWindowCornerPreference, DwmWindowCornerPreferenceRound);

        // A solid warm surface reads calmer than Mica's wallpaper tint and matches both themes.
        window.SetResourceReference(
            System.Windows.Controls.Control.BackgroundProperty,
            "WindowFallbackBrush");
    }

    public static void ApplyTheme(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var handle = new WindowInteropHelper(window).Handle;
        if (handle != nint.Zero)
        {
            TrySetDwmAttribute(handle, DwmUseImmersiveDarkMode, ThemeService.IsDark ? 1 : 0);
        }
    }

    private static bool TrySetDwmAttribute(nint handle, int attribute, int value)
    {
        try
        {
            return DwmSetWindowAttribute(handle, attribute, ref value, Marshal.SizeOf<int>()) >= 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmSetWindowAttribute(
        nint windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
