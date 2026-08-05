namespace DocPivot.Infrastructure.Storage;

public static class AtomicOutputFile
{
    public static string CreateStagingPath(string destinationPath, Guid jobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var destination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("Destination must have a parent directory.", nameof(destinationPath));
        var fileName = Path.GetFileNameWithoutExtension(destination);
        var extension = Path.GetExtension(destination);

        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $".{fileName}.{jobId:N}.tmp{extension}");
    }

    public static void Commit(string stagingPath, string destinationPath, bool overwrite = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var staging = Path.GetFullPath(stagingPath);
        var destination = Path.GetFullPath(destinationPath);
        var stagingDirectory = Path.GetDirectoryName(staging);
        var destinationDirectory = Path.GetDirectoryName(destination);

        if (!string.Equals(stagingDirectory, destinationDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Staging and destination files must be in the same directory.");
        }

        File.Move(staging, destination, overwrite);
    }
}
