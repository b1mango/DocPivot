using System.Runtime.InteropServices;

namespace DocPivot.OfficeWorker;

/// <summary>
/// Hides every top-level window owned by an Excel process as soon as it
/// appears. Excel briefly shows its main frame before COM automation can set
/// <c>Application.Visible = false</c>, which the user perceives as a flicker;
/// this helper removes that flash for the isolated worker instances only.
/// </summary>
internal static class ExcelWindowVisibility
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
