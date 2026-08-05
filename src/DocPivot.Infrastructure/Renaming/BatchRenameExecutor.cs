using System.Globalization;
using System.Security;
using System.Text.Json;
using DocPivot.Core.Contracts;
using DocPivot.Core.Documents;
using DocPivot.Infrastructure.Operations;

namespace DocPivot.Infrastructure.Renaming;

public sealed class BatchRenameExecutor : IBatchRenameExecutor
{
    private const int ManifestVersion = 1;
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        WriteIndented = true,
    };
    private readonly string _historyRoot;
    private readonly IRenameFileSystem _fileSystem;

    public BatchRenameExecutor()
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DocPivot",
                "rename-history"),
            PhysicalRenameFileSystem.Instance)
    {
    }

    internal BatchRenameExecutor(string historyRoot, IRenameFileSystem fileSystem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(historyRoot);
        ArgumentNullException.ThrowIfNull(fileSystem);
        _historyRoot = Path.GetFullPath(historyRoot);
        _fileSystem = fileSystem;
    }

    public Task<OperationExecutionResult> ExecuteAsync(
        BatchRenameExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => ExecuteCore(request, cancellationToken), cancellationToken);
    }

    public Task<OperationExecutionResult> UndoAsync(
        string manifestPath,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => UndoCore(manifestPath, cancellationToken), cancellationToken);

    private OperationExecutionResult ExecuteCore(
        BatchRenameExecutionRequest request,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<NormalizedRenameItem> items;
        try
        {
            items = NormalizeAndPreflight(request.Items);
        }
        catch (RenameExecutionException exception)
        {
            return Failure(exception.ErrorCode, exception.Message);
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            return Failure("RENAME_PREFLIGHT_FAILED", "The rename preflight could not inspect every file.");
        }

        var transaction = ExecuteTransaction(items, cancellationToken);
        if (!transaction.IsSucceeded)
        {
            return transaction.Failure!;
        }

        string manifestPath;
        try
        {
            manifestPath = WriteManifest(items, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            var rollback = RollbackCompletedTransaction(items);
            if (rollback is not null)
            {
                return rollback;
            }

            throw;
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            var rollback = RollbackCompletedTransaction(items);
            return rollback ?? Failure(
                "RENAME_MANIFEST_WRITE_FAILED",
                "The rename was rolled back because its undo manifest could not be saved.");
        }

        return OperationExecutionResult.Succeeded(
            items.Select(static item => item.TargetPath).ToArray(),
            [
                new WorkerNotice(
                    "RENAME_UNDO_AVAILABLE",
                    "The rename completed and an undo manifest was saved.",
                    "info"),
            ],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["renamedCount"] = items.Count.ToString(CultureInfo.InvariantCulture),
                ["manifestPath"] = manifestPath,
            });
    }

    private OperationExecutionResult UndoCore(
        string manifestPath,
        CancellationToken cancellationToken)
    {
        RenameUndoManifest manifest;
        try
        {
            var normalizedManifestPath = ValidateManifestPath(manifestPath);
            manifest = JsonSerializer.Deserialize<RenameUndoManifest>(
                    _fileSystem.ReadAllText(normalizedManifestPath))
                ?? throw new RenameExecutionException(
                    "RENAME_MANIFEST_INVALID",
                    "The undo manifest is empty or invalid.");
        }
        catch (RenameExecutionException exception)
        {
            return Failure(exception.ErrorCode, exception.Message);
        }
        catch (JsonException)
        {
            return Failure("RENAME_MANIFEST_INVALID", "The undo manifest is not valid JSON.");
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            return Failure("RENAME_MANIFEST_UNAVAILABLE", "The undo manifest could not be read.");
        }

        if (manifest.Version != ManifestVersion || manifest.Items.Count == 0)
        {
            return Failure("RENAME_MANIFEST_INVALID", "The undo manifest version is not supported.");
        }

        var request = new BatchRenameExecutionRequest(manifest.Items
            .Select(static item => new BatchRenameExecutionItem(
                item.RenamedPath,
                item.OriginalPath,
                item.SizeBytes,
                new DateTime(item.LastWriteTimeUtcTicks, DateTimeKind.Utc)))
            .ToArray());
        IReadOnlyList<NormalizedRenameItem> items;
        try
        {
            items = NormalizeAndPreflight(request.Items);
        }
        catch (RenameExecutionException exception)
        {
            var code = exception.ErrorCode == "RENAME_SOURCE_CHANGED"
                ? "RENAME_UNDO_SOURCE_CHANGED"
                : exception.ErrorCode;
            return Failure(code, exception.Message);
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            return Failure("RENAME_UNDO_PREFLIGHT_FAILED", "The undo preflight could not inspect every file.");
        }

        var transaction = ExecuteTransaction(items, cancellationToken);
        if (!transaction.IsSucceeded)
        {
            return transaction.Failure!;
        }

        return OperationExecutionResult.Succeeded(
            items.Select(static item => item.TargetPath).ToArray(),
            [
                new WorkerNotice(
                    "RENAME_UNDO_COMPLETED",
                    "The previous rename was undone.",
                    "info"),
            ],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["restoredCount"] = items.Count.ToString(CultureInfo.InvariantCulture),
                ["manifestPath"] = Path.GetFullPath(manifestPath),
            });
    }

    private NormalizedRenameItem[] NormalizeAndPreflight(
        IReadOnlyList<BatchRenameExecutionItem> requestItems)
    {
        if (requestItems is null ||
            requestItems.Count == 0 ||
            requestItems.Count > DocumentLimits.MaximumBatchFiles)
        {
            throw new RenameExecutionException(
                "RENAME_REQUEST_INVALID",
                $"A rename request must contain 1-{DocumentLimits.MaximumBatchFiles} files.");
        }

        var normalized = requestItems.Select(item => Normalize(item)).ToArray();
        var sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in normalized)
        {
            if (!sourcePaths.Add(item.SourcePath) || !targetPaths.Add(item.TargetPath))
            {
                throw new RenameExecutionException(
                    "RENAME_REQUEST_DUPLICATE_PATH",
                    "Rename source and target paths must be unique, ignoring case.");
            }
        }

        foreach (var item in normalized)
        {
            if (!_fileSystem.FileExists(item.SourcePath))
            {
                throw new RenameExecutionException(
                    "RENAME_SOURCE_MISSING",
                    $"A rename source no longer exists: {item.SourcePath}");
            }

            var snapshot = _fileSystem.GetSnapshot(item.SourcePath);
            if (snapshot.SizeBytes != item.ExpectedSizeBytes ||
                snapshot.LastWriteTimeUtc != item.ExpectedLastWriteTimeUtc)
            {
                throw new RenameExecutionException(
                    "RENAME_SOURCE_CHANGED",
                    $"A rename source changed after preview: {item.SourcePath}");
            }

            if (_fileSystem.DirectoryExists(item.TargetPath) ||
                (_fileSystem.FileExists(item.TargetPath) && !sourcePaths.Contains(item.TargetPath)))
            {
                throw new RenameExecutionException(
                    "RENAME_TARGET_EXISTS",
                    $"A rename target is already occupied: {item.TargetPath}");
            }
        }

        return normalized;
    }

    private static NormalizedRenameItem Normalize(BatchRenameExecutionItem item)
    {
        if (item is null ||
            string.IsNullOrWhiteSpace(item.SourcePath) ||
            string.IsNullOrWhiteSpace(item.TargetPath) ||
            item.ExpectedSizeBytes < 0 ||
            item.ExpectedLastWriteTimeUtc.Kind != DateTimeKind.Utc)
        {
            throw new RenameExecutionException(
                "RENAME_REQUEST_INVALID",
                "Every rename item requires valid paths and an explicit UTC source snapshot.");
        }

        var sourcePath = Path.GetFullPath(item.SourcePath);
        var targetPath = Path.GetFullPath(item.TargetPath);
        if (string.Equals(sourcePath, targetPath, StringComparison.Ordinal))
        {
            throw new RenameExecutionException(
                "RENAME_REQUEST_UNCHANGED",
                "Unchanged paths must not be submitted to the rename executor.");
        }

        if (!string.Equals(
                Path.GetDirectoryName(sourcePath),
                Path.GetDirectoryName(targetPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new RenameExecutionException(
                "RENAME_CROSS_DIRECTORY_BLOCKED",
                "A batch rename may only change names within the same directory.");
        }

        return new NormalizedRenameItem(
            sourcePath,
            targetPath,
            item.ExpectedSizeBytes,
            item.ExpectedLastWriteTimeUtc);
    }

    private TransactionResult ExecuteTransaction(
        IReadOnlyList<NormalizedRenameItem> items,
        CancellationToken cancellationToken)
    {
        var runtimeItems = items
            .Select((item, index) => new RuntimeRenameItem(
                item,
                CreateTemporaryPath(item.SourcePath, "stage", index),
                index))
            .ToArray();
        try
        {
            foreach (var item in runtimeItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _fileSystem.MoveFile(item.CurrentPath, item.StagingPath);
                item.CurrentPath = item.StagingPath;
            }

            foreach (var item in runtimeItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _fileSystem.MoveFile(item.CurrentPath, item.Definition.TargetPath);
                item.CurrentPath = item.Definition.TargetPath;
            }

            return TransactionResult.Succeeded();
        }
        catch (Exception exception) when (
            exception is OperationCanceledException || IsFileSystemException(exception))
        {
            var rollback = TryRollback(runtimeItems, exception);
            if (rollback is not null)
            {
                return TransactionResult.Failed(rollback);
            }

            if (exception is OperationCanceledException)
            {
                throw;
            }

            return TransactionResult.Failed(Failure(
                "RENAME_IO_FAILURE",
                "The rename failed and all moved files were restored."));
        }
    }

    private OperationExecutionResult? RollbackCompletedTransaction(
        IReadOnlyList<NormalizedRenameItem> items)
    {
        var runtimeItems = items
            .Select((item, index) => new RuntimeRenameItem(
                item,
                CreateTemporaryPath(item.SourcePath, "manifest-rollback", index),
                index)
            {
                CurrentPath = item.TargetPath,
            })
            .ToArray();
        return TryRollback(
            runtimeItems,
            new IOException("The rename manifest could not be committed."));
    }

    private OperationExecutionResult? TryRollback(
        IReadOnlyList<RuntimeRenameItem> runtimeItems,
        Exception originalException)
    {
        var rollbackFailures = new List<string>();
        var movedItems = runtimeItems
            .Where(item =>
                !string.Equals(
                    item.CurrentPath,
                    item.Definition.SourcePath,
                    StringComparison.Ordinal) &&
                _fileSystem.FileExists(item.CurrentPath))
            .ToArray();
        foreach (var item in movedItems)
        {
            try
            {
                var rollbackStagingPath = CreateTemporaryPath(
                    item.Definition.SourcePath,
                    "rollback",
                    item.Index);
                _fileSystem.MoveFile(item.CurrentPath, rollbackStagingPath);
                item.CurrentPath = rollbackStagingPath;
            }
            catch (Exception exception) when (IsFileSystemException(exception))
            {
                rollbackFailures.Add($"stage:{item.CurrentPath}:{exception.GetType().Name}");
            }
        }

        foreach (var item in movedItems.Reverse())
        {
            if (string.Equals(
                    item.CurrentPath,
                    item.Definition.SourcePath,
                    StringComparison.Ordinal) ||
                !_fileSystem.FileExists(item.CurrentPath))
            {
                continue;
            }

            try
            {
                _fileSystem.MoveFile(item.CurrentPath, item.Definition.SourcePath);
                item.CurrentPath = item.Definition.SourcePath;
            }
            catch (Exception exception) when (IsFileSystemException(exception))
            {
                rollbackFailures.Add($"restore:{item.CurrentPath}:{exception.GetType().Name}");
            }
        }

        if (rollbackFailures.Count == 0)
        {
            return null;
        }

        string? recoveryReportPath = null;
        try
        {
            recoveryReportPath = WriteRecoveryReport(runtimeItems, originalException, rollbackFailures);
        }
        catch (Exception exception) when (IsFileSystemException(exception))
        {
            rollbackFailures.Add($"report:{exception.GetType().Name}");
        }

        return new OperationExecutionResult(
            false,
            recoveryReportPath is null ? [] : [recoveryReportPath],
            [],
            recoveryReportPath is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["recoveryReportPath"] = recoveryReportPath,
                },
            "RENAME_ROLLBACK_INCOMPLETE",
            "The rename failed and automatic rollback was incomplete. Use the recovery report before changing these files again.",
            false);
    }

    private string WriteManifest(
        IReadOnlyList<NormalizedRenameItem> items,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _fileSystem.CreateDirectory(_historyRoot);
        var manifest = new RenameUndoManifest(
            ManifestVersion,
            DateTime.UtcNow,
            items.Select(item =>
            {
                var snapshot = _fileSystem.GetSnapshot(item.TargetPath);
                return new RenameUndoItem(
                    item.SourcePath,
                    item.TargetPath,
                    snapshot.SizeBytes,
                    snapshot.LastWriteTimeUtc.Ticks);
            }).ToArray());
        var manifestPath = Path.Combine(
            _historyRoot,
            $"{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.json");
        var stagingPath = $"{manifestPath}.tmp";
        try
        {
            _fileSystem.WriteAllText(
                stagingPath,
                JsonSerializer.Serialize(manifest, ManifestJsonOptions));
            cancellationToken.ThrowIfCancellationRequested();
            _fileSystem.MoveFile(stagingPath, manifestPath);
            return manifestPath;
        }
        finally
        {
            _fileSystem.DeleteFileIfExists(stagingPath);
        }
    }

    private string WriteRecoveryReport(
        IReadOnlyList<RuntimeRenameItem> items,
        Exception originalException,
        IReadOnlyList<string> rollbackFailures)
    {
        _fileSystem.CreateDirectory(_historyRoot);
        var reportPath = Path.Combine(
            _historyRoot,
            $"recovery-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.json");
        var report = new RenameRecoveryReport(
            DateTime.UtcNow,
            originalException.GetType().Name,
            rollbackFailures,
            items.Select(static item => new RenameRecoveryItem(
                item.Definition.SourcePath,
                item.Definition.TargetPath,
                item.CurrentPath)).ToArray());
        _fileSystem.WriteAllText(
            reportPath,
            JsonSerializer.Serialize(report, ManifestJsonOptions));
        return reportPath;
    }

    private string ValidateManifestPath(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new RenameExecutionException(
                "RENAME_MANIFEST_INVALID",
                "The undo manifest path is required.");
        }

        var normalizedPath = Path.GetFullPath(manifestPath);
        var normalizedRoot = _historyRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(normalizedPath), ".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new RenameExecutionException(
                "RENAME_MANIFEST_INVALID",
                "The undo manifest must be a JSON file created in the DocPivot rename history.");
        }

        return normalizedPath;
    }

    private string CreateTemporaryPath(string sourcePath, string phase, int index)
    {
        var directory = Path.GetDirectoryName(sourcePath)!;
        string path;
        do
        {
            path = Path.Combine(
                directory,
                $".docpivot-rename-{phase}-{index + 1}-{Guid.NewGuid():N}.tmp");
        }
        while (_fileSystem.FileExists(path) || _fileSystem.DirectoryExists(path));

        return path;
    }

    private static bool IsFileSystemException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or
            NotSupportedException;

    private static OperationExecutionResult Failure(string code, string message) =>
        OperationExecutionResult.Failed(code, message, false);

    private sealed record NormalizedRenameItem(
        string SourcePath,
        string TargetPath,
        long ExpectedSizeBytes,
        DateTime ExpectedLastWriteTimeUtc);

    private sealed class RuntimeRenameItem(
        NormalizedRenameItem definition,
        string stagingPath,
        int index)
    {
        public int Index { get; } = index;

        public NormalizedRenameItem Definition { get; } = definition;

        public string StagingPath { get; } = stagingPath;

        public string CurrentPath { get; set; } = definition.SourcePath;
    }

    private sealed record RenameUndoManifest(
        int Version,
        DateTime CreatedUtc,
        IReadOnlyList<RenameUndoItem> Items);

    private sealed record RenameUndoItem(
        string OriginalPath,
        string RenamedPath,
        long SizeBytes,
        long LastWriteTimeUtcTicks);

    private sealed record RenameRecoveryReport(
        DateTime CreatedUtc,
        string FailureType,
        IReadOnlyList<string> RollbackFailures,
        IReadOnlyList<RenameRecoveryItem> Items);

    private sealed record RenameRecoveryItem(
        string OriginalPath,
        string TargetPath,
        string CurrentPath);

    private sealed record TransactionResult(
        bool IsSucceeded,
        OperationExecutionResult? Failure)
    {
        public static TransactionResult Succeeded() => new(true, null);

        public static TransactionResult Failed(OperationExecutionResult failure) => new(false, failure);
    }

    private sealed class RenameExecutionException(
        string errorCode,
        string message) : Exception(message)
    {
        public string ErrorCode { get; } = errorCode;
    }
}

internal sealed record RenameFileSnapshot(
    long SizeBytes,
    DateTime LastWriteTimeUtc);

internal interface IRenameFileSystem
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    RenameFileSnapshot GetSnapshot(string path);

    void MoveFile(string sourcePath, string targetPath);

    void CreateDirectory(string path);

    void WriteAllText(string path, string content);

    string ReadAllText(string path);

    void DeleteFileIfExists(string path);
}

internal sealed class PhysicalRenameFileSystem : IRenameFileSystem
{
    public static PhysicalRenameFileSystem Instance { get; } = new();

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public RenameFileSnapshot GetSnapshot(string path)
    {
        var file = new FileInfo(path);
        return new RenameFileSnapshot(file.Length, file.LastWriteTimeUtc);
    }

    public void MoveFile(string sourcePath, string targetPath) => File.Move(sourcePath, targetPath);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void WriteAllText(string path, string content) => File.WriteAllText(path, content);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public void DeleteFileIfExists(string path) => File.Delete(path);
}
