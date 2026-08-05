namespace DocPivot.Infrastructure.Runtime;

public static class ExternalToolProbe
{
    public static ExternalToolAvailability FindOnPath(string executableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executableName);

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), executableName);
            if (File.Exists(candidate))
            {
                return new ExternalToolAvailability(executableName, true, candidate);
            }
        }

        return new ExternalToolAvailability(executableName, false, null);
    }
}

