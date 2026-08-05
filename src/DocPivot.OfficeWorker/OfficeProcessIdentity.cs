using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DocPivot.OfficeWorker;

internal static partial class OfficeProcessIdentity
{
    public static int GetProcessId(nint windowHandle)
    {
        _ = GetWindowThreadProcessId(windowHandle, out var processId);
        return checked((int)processId);
    }

    public static void TryTerminate(
        int processId,
        string expectedProcessName,
        DateTimeOffset earliestStart)
    {
        if (processId <= 0)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (!string.Equals(process.ProcessName, expectedProcessName, StringComparison.OrdinalIgnoreCase) ||
                process.StartTime.ToUniversalTime() < earliestStart.UtcDateTime)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
            process.WaitForExit(milliseconds: 5_000);
        }
        catch (ArgumentException)
        {
            // The Office process exited before timeout cleanup ran.
        }
        catch (InvalidOperationException)
        {
            // The Office process exited before timeout cleanup ran.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Cleanup is best-effort; never replace the structured timeout result.
        }
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint windowHandle, out uint processId);
}
