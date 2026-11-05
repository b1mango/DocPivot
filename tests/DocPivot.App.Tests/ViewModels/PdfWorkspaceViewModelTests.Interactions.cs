using DocPivot.Infrastructure.Preferences;
using DocPivot.Infrastructure.Operations;

namespace DocPivot.App.Tests.ViewModels;

public sealed partial class PdfWorkspaceViewModelTests
{
    [Fact]
    public void Preferences_RestoreExistingDirectoryAndPreserveTheme()
    {
        using var workspace = new TemporaryWorkspace();
        var directory = workspace.CreateDirectory("output");
        var store = new MemoryPreferencesStore(new("Dark", directory));
        using var vm = CreateViewModel(new FakePdfOperationsClient(), preferencesStore: store);
        Assert.Equal(directory, vm.OutputDirectory);
        vm.OutputDirectory = workspace.CreateDirectory("next");
        Assert.Equal(new UserPreferences("Dark", vm.OutputDirectory), store.Load());
    }

    [Fact]
    public void Preferences_MissingDirectoryKeepsDesktop()
    {
        using var vm = CreateViewModel(new FakePdfOperationsClient(),
            preferencesStore: new MemoryPreferencesStore(new(LastOutputDirectory: "missing-directory")));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), vm.OutputDirectory);
    }

    [Fact]
    public void Folder_ImportsSupportedTopLevelFilesInNaturalOrder()
    {
        using var workspace = new TemporaryWorkspace();
        var folder = workspace.CreateDirectory("input");
        workspace.CreatePdf("input/10.pdf");
        workspace.CreatePdf("input/2.pdf");
        workspace.CreatePdf("input/nested/1.pdf");
        File.WriteAllText(Path.Combine(folder, "ignore.txt"), "text");
        using var vm = CreateViewModel(new FakePdfOperationsClient());
        vm.AddPaths([folder]);
        Assert.Equal(["2.pdf", "10.pdf"], vm.Files.Select(file => file.FileName));
        vm.AddPaths([workspace.CreateDirectory("empty")]);
        Assert.Equal("文件夹中没有可添加的文件", vm.StatusMessage);
    }

    [Fact]
    public async Task UndoRemoval_RestoresSameInstanceAtOriginalPositionWithoutPreflight()
    {
        using var workspace = new TemporaryWorkspace();
        var calls = 0;
        var client = new FakePdfOperationsClient
        {
            PreflightHandler = (request, _) =>
            {
                calls++;
                return Task.FromResult(DocPivot.Infrastructure.Pdf.PdfPreflightResult.Succeeded(
                    request.InputPath, 5, hasSignatureFields: false));
            },
        };
        using var vm = CreateViewModel(client);
        vm.AddPaths([workspace.CreatePdf("1.pdf"), workspace.CreatePdf("2.pdf"), workspace.CreatePdf("3.pdf")]);
        await vm.WaitForPdfPreflightAsync();
        var file = vm.Files[1];
        vm.RemoveFileCommand.Execute(file);
        Assert.Equal("已移除 2.pdf", vm.UndoableRemovalText);
        vm.UndoRemoveCommand.Execute(null);
        Assert.Same(file, vm.Files[1]);
        Assert.Equal(3, calls);
        Assert.False(vm.HasUndoableRemoval);
    }

    [Fact]
    public async Task UndoRemoval_RestartsCanceledLoadingPreflight()
    {
        using var workspace = new TemporaryWorkspace();
        var calls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var vm = CreateViewModel(new FakePdfOperationsClient
        {
            PreflightHandler = async (request, token) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    started.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                return DocPivot.Infrastructure.Pdf.PdfPreflightResult.Succeeded(
                    request.InputPath, 5, hasSignatureFields: false);
            },
        });
        vm.AddPaths([workspace.CreatePdf("loading.pdf")]);
        await started.Task.WaitAsync(TestTimeouts.Signal);
        var file = Assert.Single(vm.Files);
        vm.RemoveFileCommand.Execute(file);
        vm.UndoRemoveCommand.Execute(null);
        await vm.WaitForPdfPreflightAsync();
        Assert.Same(file, Assert.Single(vm.Files));
        Assert.False(file.IsPdfPreflightLoading);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void UndoRemoval_ExpiresAfterFiveSecondsAndNewRemovalReplacesPrevious()
    {
        using var workspace = new TemporaryWorkspace();
        var clock = new ManualTimeProvider();
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            using var vm = CreateViewModel(new FakePdfOperationsClient(), timeProvider: clock);
            vm.AddPaths([workspace.CreatePdf("1.pdf"), workspace.CreatePdf("2.pdf")]);
            var first = vm.Files[0];
            var second = vm.Files[1];
            vm.RemoveFileCommand.Execute(first);
            clock.Advance(TimeSpan.FromSeconds(4));
            Assert.True(vm.HasUndoableRemoval);
            vm.RemoveFileCommand.Execute(second);
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True(vm.HasUndoableRemoval);
            vm.UndoRemoveCommand.Execute(null);
            Assert.Same(second, Assert.Single(vm.Files));
            vm.RemoveFileCommand.Execute(second);
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.False(vm.HasUndoableRemoval);
            Assert.False(vm.UndoRemoveCommand.CanExecute(null));
        }
        finally { SynchronizationContext.SetSynchronizationContext(context); }
    }

    [Fact]
    public async Task CompletedMerge_OpensAndRevealsSingleOutputAndAddingClearsResult()
    {
        using var workspace = new TemporaryWorkspace();
        var shell = new FakeShellService();
        using var vm = CreateViewModel(new FakePdfOperationsClient
        {
            MergeHandler = (request, _) => Task.FromResult(OperationExecutionResult.Succeeded([request.OutputPath])),
        }, shell);
        vm.OutputDirectory = workspace.CreateDirectory("output");
        vm.AddPaths([workspace.CreatePdf("1.pdf"), workspace.CreatePdf("2.pdf")]);
        await vm.WaitForPdfPreflightAsync();
        await vm.StartProcessingCommand.ExecuteAsync(null);
        Assert.True(vm.HasCompletedResult);
        Assert.True(vm.CanOpenCompletedFile);
        Assert.Equal("已合并", vm.CompletedResultTitle);
        vm.OpenCompletedResultCommand.Execute(null);
        vm.RevealCompletedResultCommand.Execute(null);
        Assert.Equal(vm.Files[0].OutputPath, shell.OpenedFile);
        Assert.Equal(shell.OpenedFile, shell.RevealedFile);
        vm.AddPaths([workspace.CreatePdf("3.pdf")]);
        Assert.False(vm.HasCompletedResult);
        Assert.False(vm.OpenCompletedResultCommand.CanExecute(null));
    }

    private sealed class MemoryPreferencesStore(UserPreferences preferences) : IUserPreferencesStore
    {
        public UserPreferences Load() => preferences;
        public void Save(UserPreferences value) => preferences = value;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private TimeSpan _elapsed;
        private readonly List<ManualTimer> _timers = [];
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan duration)
        {
            _elapsed += duration;
            foreach (var timer in _timers.ToArray()) timer.Fire();
        }
        private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
        {
            private TimeSpan _due;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _due = clock._elapsed + dueTime;
                return !_disposed;
            }
            public void Fire()
            {
                if (!_disposed && clock._elapsed >= _due) { _disposed = true; callback(state); }
            }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
