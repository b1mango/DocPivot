using DocPivot.Infrastructure.Renaming;

namespace DocPivot.Infrastructure.Tests.Renaming;

public sealed class BatchRenameExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_SwapsNamesThroughTwoPhaseTransactionAndWritesManifest()
    {
        using var workspace = new TemporaryWorkspace();
        var first = workspace.CreateFile("one.pdf", "ONE");
        var second = workspace.CreateFile("two.pdf", "TWO");
        var executor = workspace.CreateExecutor();

        var result = await executor.ExecuteAsync(new BatchRenameExecutionRequest(
        [
            Item(first, second),
            Item(second, first),
        ]));

        Assert.True(result.IsSucceeded, result.ErrorMessage);
        Assert.Equal("TWO", File.ReadAllText(first));
        Assert.Equal("ONE", File.ReadAllText(second));
        Assert.Equal("2", result.Metrics["renamedCount"]);
        Assert.True(File.Exists(result.Metrics["manifestPath"]));
        Assert.Contains(result.Notices, static notice => notice.Code == "RENAME_UNDO_AVAILABLE");
        AssertNoTransactionFiles(workspace.Root);
    }

    [Fact]
    public async Task ExecuteAsync_AllowsCaseOnlyRename()
    {
        using var workspace = new TemporaryWorkspace();
        var source = workspace.CreateFile("report.pdf", "DATA");
        var target = Path.Combine(workspace.Root, "REPORT.pdf");
        var executor = workspace.CreateExecutor();

        var result = await executor.ExecuteAsync(new BatchRenameExecutionRequest([Item(source, target)]));

        Assert.True(result.IsSucceeded, result.ErrorMessage);
        Assert.Equal(
            "REPORT.pdf",
            Path.GetFileName(Assert.Single(Directory.EnumerateFiles(workspace.Root, "*.pdf"))));
        Assert.Equal("DATA", File.ReadAllText(target));
        AssertNoTransactionFiles(workspace.Root);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSourceChangedAfterPreview_RejectsWithoutMovingAnything()
    {
        using var workspace = new TemporaryWorkspace();
        var source = workspace.CreateFile("source.pdf", "BEFORE");
        var target = Path.Combine(workspace.Root, "target.pdf");
        var requestItem = Item(source, target);
        File.AppendAllText(source, "-CHANGED");
        var executor = workspace.CreateExecutor();

        var result = await executor.ExecuteAsync(new BatchRenameExecutionRequest([requestItem]));

        Assert.False(result.IsSucceeded);
        Assert.Equal("RENAME_SOURCE_CHANGED", result.ErrorCode);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(target));
        AssertNoTransactionFiles(workspace.Root);
    }

    [Fact]
    public async Task ExecuteAsync_WhenSecondPhaseFails_RollsBackEverySource()
    {
        using var workspace = new TemporaryWorkspace();
        var first = workspace.CreateFile("one.pdf", "ONE");
        var second = workspace.CreateFile("two.pdf", "TWO");
        var fileSystem = new FaultInjectingFileSystem(failMoveNumbers: [4]);
        var executor = workspace.CreateExecutor(fileSystem);

        var result = await executor.ExecuteAsync(new BatchRenameExecutionRequest(
        [
            Item(first, Path.Combine(workspace.Root, "first.pdf")),
            Item(second, Path.Combine(workspace.Root, "second.pdf")),
        ]));

        Assert.False(result.IsSucceeded);
        Assert.Equal("RENAME_IO_FAILURE", result.ErrorCode);
        Assert.Equal("ONE", File.ReadAllText(first));
        Assert.Equal("TWO", File.ReadAllText(second));
        Assert.False(File.Exists(Path.Combine(workspace.Root, "first.pdf")));
        Assert.False(File.Exists(Path.Combine(workspace.Root, "second.pdf")));
        AssertNoTransactionFiles(workspace.Root);
    }

    [Fact]
    public async Task ExecuteAsync_WhenRollbackAlsoFails_WritesRecoveryReport()
    {
        using var workspace = new TemporaryWorkspace();
        var first = workspace.CreateFile("one.pdf", "ONE");
        var second = workspace.CreateFile("two.pdf", "TWO");
        var fileSystem = new FaultInjectingFileSystem(failMoveNumbers: [4, 5]);
        var executor = workspace.CreateExecutor(fileSystem);

        var result = await executor.ExecuteAsync(new BatchRenameExecutionRequest(
        [
            Item(first, Path.Combine(workspace.Root, "first.pdf")),
            Item(second, Path.Combine(workspace.Root, "second.pdf")),
        ]));

        Assert.False(result.IsSucceeded);
        Assert.Equal("RENAME_ROLLBACK_INCOMPLETE", result.ErrorCode);
        var report = Assert.Single(result.Artifacts);
        Assert.True(File.Exists(report));
        Assert.Contains("RollbackFailures", File.ReadAllText(report), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UndoAsync_RestoresOriginalNamesAndRejectsExternallyModifiedTargets()
    {
        using var workspace = new TemporaryWorkspace();
        var source = workspace.CreateFile("source.pdf", "DATA");
        var target = Path.Combine(workspace.Root, "renamed.pdf");
        var executor = workspace.CreateExecutor();
        var executed = await executor.ExecuteAsync(
            new BatchRenameExecutionRequest([Item(source, target)]));
        Assert.True(executed.IsSucceeded, executed.ErrorMessage);
        var manifestPath = executed.Metrics["manifestPath"];

        var undone = await executor.UndoAsync(manifestPath);

        Assert.True(undone.IsSucceeded, undone.ErrorMessage);
        Assert.Equal("DATA", File.ReadAllText(source));
        Assert.False(File.Exists(target));
        Assert.Contains(undone.Notices, static notice => notice.Code == "RENAME_UNDO_COMPLETED");

        var secondTarget = Path.Combine(workspace.Root, "renamed-again.pdf");
        var secondExecution = await executor.ExecuteAsync(
            new BatchRenameExecutionRequest([Item(source, secondTarget)]));
        Assert.True(secondExecution.IsSucceeded, secondExecution.ErrorMessage);
        File.AppendAllText(secondTarget, "-EXTERNAL");

        var rejectedUndo = await executor.UndoAsync(secondExecution.Metrics["manifestPath"]);

        Assert.False(rejectedUndo.IsSucceeded);
        Assert.Equal("RENAME_UNDO_SOURCE_CHANGED", rejectedUndo.ErrorCode);
        Assert.False(File.Exists(source));
        Assert.Equal("DATA-EXTERNAL", File.ReadAllText(secondTarget));
    }

    [Fact]
    public async Task UndoAsync_RejectsManifestOutsideConfiguredHistoryRoot()
    {
        using var workspace = new TemporaryWorkspace();
        var outsideManifest = workspace.CreateFile("outside.json", "{}");
        var executor = workspace.CreateExecutor();

        var result = await executor.UndoAsync(outsideManifest);

        Assert.False(result.IsSucceeded);
        Assert.Equal("RENAME_MANIFEST_INVALID", result.ErrorCode);
    }

    private static BatchRenameExecutionItem Item(string sourcePath, string targetPath)
    {
        var source = new FileInfo(sourcePath);
        return new BatchRenameExecutionItem(
            source.FullName,
            targetPath,
            source.Length,
            source.LastWriteTimeUtc);
    }

    private static void AssertNoTransactionFiles(string root) =>
        Assert.Empty(Directory.EnumerateFiles(root, ".docpivot-rename-*.tmp"));

    private sealed class TemporaryWorkspace : IDisposable
    {
        public TemporaryWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "DocPivot.Tests", Guid.NewGuid().ToString("N"));
            HistoryRoot = Path.Combine(Root, "history");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        private string HistoryRoot { get; }

        public string CreateFile(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public BatchRenameExecutor CreateExecutor(IRenameFileSystem? fileSystem = null) =>
            new(HistoryRoot, fileSystem ?? PhysicalRenameFileSystem.Instance);

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class FaultInjectingFileSystem(IReadOnlyCollection<int> failMoveNumbers)
        : IRenameFileSystem
    {
        private int _moveCount;

        public bool FileExists(string path) => File.Exists(path);

        public bool DirectoryExists(string path) => Directory.Exists(path);

        public RenameFileSnapshot GetSnapshot(string path)
        {
            var file = new FileInfo(path);
            return new RenameFileSnapshot(file.Length, file.LastWriteTimeUtc);
        }

        public void MoveFile(string sourcePath, string targetPath)
        {
            var moveNumber = Interlocked.Increment(ref _moveCount);
            if (failMoveNumbers.Contains(moveNumber))
            {
                throw new IOException($"Injected move failure #{moveNumber}.");
            }

            File.Move(sourcePath, targetPath);
        }

        public void CreateDirectory(string path) => Directory.CreateDirectory(path);

        public void WriteAllText(string path, string content) => File.WriteAllText(path, content);

        public string ReadAllText(string path) => File.ReadAllText(path);

        public void DeleteFileIfExists(string path) => File.Delete(path);
    }
}
