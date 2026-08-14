using System.Runtime.InteropServices;

namespace DocPivot.OfficeWorker;

/// <summary>
/// Best-effort guard that hides any visible top-level window owned by an
/// Excel process. COM-activated Excel instances start invisible, so this only
/// acts as a safety net for windows Excel might show on its own (for example
/// after an unexpected dialog).
/// </summary>
internal static partial class ExcelWindowVisibility
{
    public static void HideAllWindows(int processId)
    {
        if (processId <= 0)
        {
            return;
        }

        _ = EnumWindows((window, _) =>
        {
            if (OfficeProcessIdentity.GetProcessId(window) == processId &&
                IsWindowVisible(window))
            {
                HideWindow(window);
            }

            return (nint)1;
        }, 0);
    }

    public static void HideWindow(nint window)
    {
        if (window == 0)
        {
            return;
        }

        // ShowWindowAsync posts the hide request, which is safe from any thread
        // and takes effect before the window's first paint completes.
        _ = ShowWindowAsync(window, SwHide);
    }

    private const int SwHide = 0;

    private delegate nint EnumWindowsProcedure(nint window, nint parameter);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(EnumWindowsProcedure callback, nint parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindowAsync(nint window, int command);
}
