namespace DocPivot.Infrastructure.Storage;

public sealed class JobWorkspace : IDisposable
{
    private readonly string _normalizedRoot;
    private bool _disposed;

    private JobWorkspace(string normalizedRoot, string path)
    {
        _normalizedRoot = normalizedRoot;
        Path = path;
    }

    public string Path { get; }

    public static JobWorkspace Create(string baseDirectory, Guid jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        var normalizedRoot = System.IO.Path.GetFullPath(baseDirectory)
            .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var workspacePath = System.IO.Path.Combine(normalizedRoot, jobId.ToString("N"));

        Directory.CreateDirectory(workspacePath);
        return new JobWorkspace(normalizedRoot, workspacePath);
    }

    public string GetIntermediatePath(string fileName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        if (!string.Equals(fileName, System.IO.Path.GetFileName(fileName), StringComparison.Ordinal))
        {
            throw new ArgumentException("Intermediate file name must not contain directory segments.", nameof(fileName));
        }

        return System.IO.Path.Combine(Path, fileName);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!Directory.Exists(Path))
        {
            return;
        }

        var normalizedPath = System.IO.Path.GetFullPath(Path);
        var expectedPrefix = _normalizedRoot + System.IO.Path.DirectorySeparatorChar;
        if (!normalizedPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to delete a workspace outside its configured root.");
        }

        Directory.Delete(normalizedPath, recursive: true);
    }
}

