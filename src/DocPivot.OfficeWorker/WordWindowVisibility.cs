using System.Runtime.InteropServices;

namespace DocPivot.OfficeWorker;

/// <summary>
/// Hides every window Word created for the current automation instance. Word
/// shows its main frame during COM activation even when the document and the
/// application are opened hidden; hiding the process windows suppresses the
/// flicker users reported for Office-to-PDF conversions.
/// </summary>
internal static class WordWindowVisibility
{
    public static void HideAllWindows(dynamic application)
    {
        try
        {
            var processId = OfficeProcessIdentity.GetProcessId((nint)(int)application.Hwnd);
            if (processId > 0)
            {
                HideWindows(processId);
            }
        }
        catch (COMException)
        {
            // The application may not expose its handle yet; the Visible=false
            // assignment below remains the primary hiding mechanism.
        }
    }

    private static void HideWindows(int processId)
    {
        _ = EnumWindows((window, _) =>
        {
            if (OfficeProcessIdentity.GetProcessId(window) == processId &&
                IsWindowVisible(window))
            {
                ShowWindow(window, ShowWindowCommand.Hide);
            }

            return (nint)1;
        }, 0);
    }

    private delegate nint EnumWindowsProcedure(nint window, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProcedure callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, ShowWindowCommand command);

    private enum ShowWindowCommand
    {
        Hide = 0,
    }
}
