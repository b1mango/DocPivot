using DocPivot.App.Services;
using DocPivot.App.ViewModels;
using DocPivot.Core.Contracts;
using DocPivot.Core.Documents;
using DocPivot.Core.Renaming;
using DocPivot.Infrastructure.Office;
using DocPivot.Infrastructure.Operations;
using DocPivot.Infrastructure.Renaming;

namespace DocPivot.App.Tests.ViewModels;

public sealed class BatchRenameWorkspaceViewModelTests
{
    [Fact]
    public void RenamePreview_AppliesRulesSortingManualOverridesAndConflictValidation()
    {
        using var workspace = new TemporaryWorkspace();
        var firstPath = workspace.CreateFile("季度报告.docx");
        var secondPath = workspace.CreateFile("季度预算.docx");
        using var viewModel = CreateViewModel(new FakeBatchRenameExecutor());
        SelectRenameTool(viewModel);
        viewModel.AddPaths([secondPath, firstPath]);

        viewModel.RenameSearchText = "季度";
        viewModel.RenameReplacementText = "2026";
        viewModel.RenameInsertText = "_归档";
        viewModel.SelectedRenameInsertPosition = Assert.Single(
            viewModel.RenamePositions,
            static option => option.Position == RenameInsertPosition.End);
        viewModel.RenameNumberingEnabled = true;
        viewModel.RenameNumberingStart = 10;
        viewModel.RenameNumberingStep = 2;
        viewModel.RenameNumberingPadding = 3;
        viewModel.RenameNumberingPrefix = "_";
        viewModel.SelectedRenameSortMode = Assert.Single(
            viewModel.RenameSortModes,
            static option => option.Mode == RenameSortMode.FileName);

        Assert.Collection(
            viewModel.Files,
            first =>
            {
                Assert.Equal(firstPath, first.FullPath);
                Assert.Equal("2026报告_归档_010.docx", first.RenamePreviewName);
                Assert.True(first.RenameWillChange);
            },
            second =>
            {
                Assert.Equal(secondPath, second.FullPath);
                Assert.Equal("2026预算_归档_012.docx", second.RenamePreviewName);
                Assert.True(second.RenameWillChange);
            });
        Assert.Equal(2, viewModel.RenameChangedCount);
        Assert.True(viewModel.StartProcessingCommand.CanExecute(null));

        viewModel.Files[0].RenamePreviewName = "统一名称.docx";
        viewModel.Files[1].RenamePreviewName = "统一名称.docx";

        Assert.True(viewModel.Files[0].IsRenameManuallyEdited);
        Assert.Equal(2, viewModel.RenameConflictCount);
        Assert.False(viewModel.StartProcessingCommand.CanExecute(null));

        viewModel.ClearAllRenameOverridesCommand.Execute(null);

        Assert.Equal(0, viewModel.RenameConflictCount);
        Assert.All(viewModel.Files, static file => Assert.False(file.IsRenameManuallyEdited));
        Assert.True(viewModel.StartProcessingCommand.CanExecute(null));
    }

    [Fact]
    public void AddFolder_HonorsRecursionAndSupportedFormatFilter()
    {
        using var workspace = new TemporaryWorkspace();
        var rootFile = workspace.CreateFile(Path.Combine("input", "root.pdf"));
        workspace.CreateFile(Path.Combine("input", "ignored.txt"));
        var nestedFile = workspace.CreateFile(Path.Combine("input", "nested", "child.xlsx"));
        var inputDirectory = Path.GetDirectoryName(rootFile)!;
        using var viewModel = CreateViewModel(new FakeBatchRenameExecutor());
        SelectRenameTool(viewModel);
        viewModel.RenameIncludeSubdirectories = false;

        viewModel.AddPaths([inputDirectory]);

        Assert.Equal(rootFile, Assert.Single(viewModel.Files).FullPath);

        viewModel.ClearQueueCommand.Execute(null);
        viewModel.RenameIncludeSubdirectories = true;
        viewModel.AddPaths([inputDirectory]);

        Assert.Equal(2, viewModel.Files.Count);
        Assert.Contains(viewModel.Files, file => file.FullPath == rootFile);
        Assert.Contains(viewModel.Files, file => file.FullPath == nestedFile);
    }

    [Fact]
    public async Task ExecuteAndUndo_UsePreviewSnapshotAndExposeAwaitableUndoTask()
    {
        using var workspace = new TemporaryWorkspace();
        var firstPath = workspace.CreateFile("old-one.pdf");
        var secondPath = workspace.CreateFile("old-two.pdf");
        var manifestPath = Path.Combine(workspace.Root, "rename-manifest.json");
        BatchRenameExecutionRequest? capturedRequest = null;
        var undoStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var undoCompletion = new TaskCompletionSource<OperationExecutionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new FakeBatchRenameExecutor
        {
            ExecuteHandler = (request, _) =>
            {
                capturedRequest = request;
                return Task.FromResult(OperationExecutionResult.Succeeded(
                    request.Items.Select(static item => item.TargetPath).ToArray(),
                    metrics: new Dictionary<string, string>
                    {
                        ["manifestPath"] = manifestPath,
                    }));
            },
            UndoHandler = (path, _) =>
            {
                Assert.Equal(manifestPath, path);
                undoStarted.TrySetResult();
                return undoCompletion.Task;
            },
        };
        using var viewModel = CreateViewModel(executor);
        SelectRenameTool(viewModel);
        viewModel.RenameSearchText = "old";
        viewModel.RenameReplacementText = "new";
        viewModel.AddPaths([firstPath, secondPath]);

        await viewModel.StartProcessingCommand.ExecuteAsync(null);

        Assert.NotNull(capturedRequest);
        Assert.Collection(
            capturedRequest.Items,
            first => Assert.EndsWith("new-one.pdf", first.TargetPath, StringComparison.Ordinal),
            second => Assert.EndsWith("new-two.pdf", second.TargetPath, StringComparison.Ordinal));
        Assert.All(viewModel.Files, static file => Assert.True(file.IsSucceeded));
        Assert.Equal(manifestPath, viewModel.LastRenameManifestPath);
        Assert.True(viewModel.HasRenameUndo);

        var undoCommandTask = viewModel.UndoLastRenameCommand.ExecuteAsync(null);
        await undoStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var waitTask = viewModel.WaitForRenameUndoCompletionAsync();
        Assert.False(waitTask.IsCompleted);
        undoCompletion.SetResult(OperationExecutionResult.Succeeded([firstPath, secondPath]));
        await undoCommandTask;
        await waitTask;

        Assert.False(viewModel.HasRenameUndo);
        Assert.Null(viewModel.LastRenameManifestPath);
        Assert.All(viewModel.Files, static file => Assert.Equal("已撤销重命名", file.StatusText));
    }

    [Fact]
    public void ManualPaste_MapsLinesAndKeepsExtensionsProtectedByDefault()
    {
        using var workspace = new TemporaryWorkspace();
        var firstPath = workspace.CreateFile("第一份.docx");
        var secondPath = workspace.CreateFile("第二份.xlsx");
        using var viewModel = CreateViewModel(new FakeBatchRenameExecutor());
        SelectRenameTool(viewModel);
        viewModel.AddPaths([firstPath, secondPath]);
        viewModel.RenameManualNamesText = "甲.docx\r\n乙.pdf\r\n";

        viewModel.ApplyRenameManualNamesCommand.Execute(null);

        Assert.Equal("甲.docx", viewModel.Files[0].RenamePreviewName);
        Assert.Equal("乙.pdf", viewModel.Files[1].RenamePreviewName);
        Assert.Equal(1, viewModel.RenameInvalidCount);
        Assert.Equal("RENAME_EXTENSION_PROTECTED", viewModel.Files[1].RenameValidationCode);
        Assert.False(viewModel.StartProcessingCommand.CanExecute(null));

        viewModel.RenameIncludeExtension = true;

        Assert.Equal(0, viewModel.RenameInvalidCount);
        Assert.True(viewModel.StartProcessingCommand.CanExecute(null));
    }

    private static WorkspaceViewModel CreateViewModel(IBatchRenameExecutor executor) => new(
        new FakeFilePickerService(),
        new FakeOfficeWorkerClient(),
        new FakeShellService(),
        batchRenameExecutor: executor);

    private static void SelectRenameTool(WorkspaceViewModel viewModel)
    {
        viewModel.SelectedTool = Assert.Single(
            viewModel.Tools,
            static tool => tool.Operation == DocumentOperation.BatchRename);
    }

    private sealed class FakeBatchRenameExecutor : IBatchRenameExecutor
    {
        public Func<BatchRenameExecutionRequest, CancellationToken, Task<OperationExecutionResult>> ExecuteHandler
            { get; init; } = static (request, _) => Task.FromResult(
                OperationExecutionResult.Succeeded(
                    request.Items.Select(static item => item.TargetPath).ToArray()));

        public Func<string, CancellationToken, Task<OperationExecutionResult>> UndoHandler
            { get; init; } = static (_, _) => Task.FromResult(OperationExecutionResult.Succeeded([]));

        public Task<OperationExecutionResult> ExecuteAsync(
            BatchRenameExecutionRequest request,
            CancellationToken cancellationToken = default) =>
            ExecuteHandler(request, cancellationToken);

        public Task<OperationExecutionResult> UndoAsync(
            string manifestPath,
            CancellationToken cancellationToken = default) =>
            UndoHandler(manifestPath, cancellationToken);
    }

    private sealed class FakeOfficeWorkerClient : IOfficeWorkerClient
    {
        public Task<OfficeEngineStatus> ProbeAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new OfficeEngineStatus(true, "Office", "16.0", "x64", null));

        public Task<ExcelWorksheetListResult> ListExcelWorksheetsAsync(
            string inputPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ExcelWorksheetListResult.Succeeded([]));

        public Task<OfficeConversionResult> ConvertAsync(
            string inputPath,
            string outputPath,
            OfficeConversionOptions? options = null,
            IProgress<WorkerProgressMessage>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(OfficeConversionResult.Succeeded([outputPath]));
    }

    private sealed class FakeFilePickerService : IFilePickerService
    {
        public IReadOnlyList<string> PickFiles(DocumentOperation operation) => [];

        public string? PickFolder(string initialDirectory) => null;
    }

    private sealed class FakeShellService : IShellService
    {
        public void OpenFile(string path)
        {
        }

        public void OpenFolder(string path)
        {
        }
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        public TemporaryWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "DocPivot.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string CreateFile(string relativePath)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fixture");
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
