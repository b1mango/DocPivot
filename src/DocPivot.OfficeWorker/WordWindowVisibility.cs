using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

namespace DocPivot.OfficeWorker;

/// <summary>
/// Hides every window Word created for the current automation instance. Word
/// shows its main frame during COM activation even when the document and the
/// application are opened hidden; hiding the process windows suppresses the
/// flicker users reported for Office-to-PDF conversions.
/// </summary>
internal static partial class WordWindowVisibility
{
    public static void HideWindows(int processId)
    {
        if (processId <= 0)
        {
            return;
        }

        _ = EnumWindows((window, parameter) =>
        {
            if (OfficeProcessIdentity.GetProcessId(window) == processId &&
                IsWindowVisible(window))
            {
                _ = ShowWindowAsync(window, (int)ShowWindowCommand.Hide);
            }

            return (nint)1;
        }, 0);
    }

    /// <summary>
    /// Reads the owning process id from a Word window. Word's
    /// <c>Application.Hwnd</c> is not reliably resolvable through the .NET COM
    /// binder, but a document window's <c>Hwnd</c> is; failures fall back to 0
    /// and the caller keeps the <c>Visible = false</c> assignment as its
    /// primary hiding mechanism.
    /// </summary>
    public static int TryGetWindowProcessId(dynamic window)
    {
        try
        {
            var handle = (nint)(int)window.Hwnd;
            return handle == 0 ? 0 : OfficeProcessIdentity.GetProcessId(handle);
        }
        catch (Exception exception) when (
            exception is COMException or InvalidCastException or RuntimeBinderException)
        {
            return 0;
        }
    }

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

    private enum ShowWindowCommand
    {
        Hide = 0,
    }
}
