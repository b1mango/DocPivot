using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("DocPivot.Infrastructure.Tests")]

namespace DocPivot.Infrastructure.Storage;

public static class AtomicOutputBatch
{
    /// <summary>
    /// Moves staged files to their destinations in input order after validating the entire batch.
    /// </summary>
    /// <remarks>
    /// If a move fails, destinations already created by this invocation are deleted in reverse order.
    /// Their staged files have already been consumed; the failed and unattempted staged files remain.
    /// Rollback failures are reported together with the original commit failure.
    /// </remarks>
    public static IReadOnlyList<string> Commit(
        IEnumerable<(string StagingPath, string DestinationPath)> outputs) =>
        Commit(outputs, PhysicalFileSystem.Instance);

    internal static IReadOnlyList<string> Commit(
        IEnumerable<(string StagingPath, string DestinationPath)> outputs,
        IAtomicOutputBatchFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(fileSystem);

        var normalizedOutputs = Normalize(outputs);
        Preflight(normalizedOutputs, fileSystem);

        var committedDestinations = new List<string>(normalizedOutputs.Count);
        try
        {
            foreach (var output in normalizedOutputs)
            {
                fileSystem.MoveFile(output.StagingPath, output.DestinationPath);
                committedDestinations.Add(output.DestinationPath);
            }
        }
        catch (Exception commitException)
        {
            var rollbackExceptions = Rollback(committedDestinations, fileSystem);
            if (rollbackExceptions.Count > 0)
            {
                rollbackExceptions.Insert(0, commitException);
                throw new AggregateException(
                    "Atomic output batch commit failed and rollback was incomplete.",
                    rollbackExceptions);
            }

            throw;
        }

        return committedDestinations.AsReadOnly();
    }

    private static List<NormalizedOutput> Normalize(
        IEnumerable<(string StagingPath, string DestinationPath)> outputs)
    {
        var normalizedOutputs = new List<NormalizedOutput>();
        var uniquePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var output in outputs)
        {
            if (string.IsNullOrWhiteSpace(output.StagingPath))
            {
                throw new ArgumentException("A staging path must not be empty.", nameof(outputs));
            }

            if (string.IsNullOrWhiteSpace(output.DestinationPath))
            {
                throw new ArgumentException("A destination path must not be empty.", nameof(outputs));
            }

            var stagingPath = Path.GetFullPath(output.StagingPath);
            var destinationPath = Path.GetFullPath(output.DestinationPath);
            var stagingDirectory = Path.GetDirectoryName(stagingPath);
            var destinationDirectory = Path.GetDirectoryName(destinationPath);

            if (stagingDirectory is null || destinationDirectory is null)
            {
                throw new ArgumentException("Every output path must have a parent directory.", nameof(outputs));
            }

            if (!string.Equals(stagingDirectory, destinationDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Each staging file and destination file must be in the same directory.");
            }

            if (!uniquePaths.Add(stagingPath) || !uniquePaths.Add(destinationPath))
            {
                throw new InvalidOperationException(
                    "Atomic output batch paths must be unique, ignoring letter casing.");
            }

            normalizedOutputs.Add(new NormalizedOutput(stagingPath, destinationPath));
        }

        return normalizedOutputs;
    }

    private static void Preflight(
        IReadOnlyList<NormalizedOutput> outputs,
        IAtomicOutputBatchFileSystem fileSystem)
    {
        foreach (var output in outputs)
        {
            if (!fileSystem.FileExists(output.StagingPath))
            {
                throw new FileNotFoundException("A staged output file does not exist.", output.StagingPath);
            }

            if (fileSystem.FileExists(output.DestinationPath) ||
                fileSystem.DirectoryExists(output.DestinationPath))
            {
                throw new IOException($"An output destination already exists: {output.DestinationPath}");
            }
        }
    }

    private static List<Exception> Rollback(
        List<string> committedDestinations,
        IAtomicOutputBatchFileSystem fileSystem)
    {
        var rollbackExceptions = new List<Exception>();
        for (var index = committedDestinations.Count - 1; index >= 0; index--)
        {
            try
            {
                fileSystem.DeleteFile(committedDestinations[index]);
            }
            catch (Exception exception)
            {
                rollbackExceptions.Add(exception);
            }
        }

        return rollbackExceptions;
    }

    private readonly record struct NormalizedOutput(string StagingPath, string DestinationPath);

    private sealed class PhysicalFileSystem : IAtomicOutputBatchFileSystem
    {
        public static PhysicalFileSystem Instance { get; } = new();

        public bool FileExists(string path) => File.Exists(path);

        public bool DirectoryExists(string path) => Directory.Exists(path);

        public void MoveFile(string stagingPath, string destinationPath) =>
            File.Move(stagingPath, destinationPath);

        public void DeleteFile(string path) => File.Delete(path);
    }
}

internal interface IAtomicOutputBatchFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    void MoveFile(string stagingPath, string destinationPath);

    void DeleteFile(string path);
}
