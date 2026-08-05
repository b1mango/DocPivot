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
    private const int DwmSystemBackdropType = 38;
    private const int DwmSystemBackdropTypeMainWindow = 2;

    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero)
        {
            return;
        }

        TrySetDwmAttribute(handle, DwmUseImmersiveDarkMode, 1);
        TrySetDwmAttribute(handle, DwmWindowCornerPreference, DwmWindowCornerPreferenceRound);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) ||
            !TrySetDwmAttribute(handle, DwmSystemBackdropType, DwmSystemBackdropTypeMainWindow))
        {
            window.SetResourceReference(
                System.Windows.Controls.Control.BackgroundProperty,
                "WindowFallbackBrush");
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
