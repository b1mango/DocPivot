namespace DocPivot.Infrastructure.Pdf;

/// <summary>
/// Resolves the directory that contains the pinned offline PDF runtimes.
/// Single-file apps extract content into a temporary directory at startup,
/// while the executable itself can remain in a directory without vendor files.
/// </summary>
public static class PdfRuntimeDistributionRoot
{
    private const int MaximumParentDepth = 8;

    public static string Resolve(string configuredRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredRoot);

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddCandidate(candidates, configuredRoot);
        AddCandidate(candidates, AppContext.BaseDirectory);
        AddCandidate(candidates, Path.GetDirectoryName(Environment.ProcessPath));
        AddCandidate(candidates, Environment.CurrentDirectory);

        foreach (var candidate in candidates)
        {
            var directory = new DirectoryInfo(candidate);
            for (var depth = 0;
                 directory is not null && depth < MaximumParentDepth;
                 depth++, directory = directory.Parent)
            {
                if (IsRuntimeRoot(directory.FullName))
                {
                    return directory.FullName;
                }
            }
        }

        return Path.GetFullPath(configuredRoot);
    }

    private static bool IsRuntimeRoot(string directory) =>
        File.Exists(Path.Combine(directory, QpdfToolProbe.RelativeExecutablePath)) &&
        File.Exists(Path.Combine(
            directory,
            Ghostscript.GhostscriptPdfOptimizer.RelativeExecutablePath));

    private static void AddCandidate(HashSet<string> candidates, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            candidates.Add(Path.GetFullPath(path));
        }
    }
}
