using System.Diagnostics;

namespace DocPivot.App.Services;

public sealed class WindowsShellService : IShellService
{
    public void OpenFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public void RevealInFolder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    public void OpenFolder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
