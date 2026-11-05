using DocPivot.App.ViewModels;
using DocPivot.Core.Documents;
using DocPivot.Core.Pdf;
using DocPivot.Infrastructure.Operations;
using DocPivot.Infrastructure.Preferences;
using DocPivot.Infrastructure.Pdf;
using DocPivot.Infrastructure.Renaming;

namespace DocPivot.App.Tests.ViewModels;

public sealed partial class PdfWorkspaceViewModelTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RecentOutputs_PublishedResultsRecordFileOrFolder(int count)
    {
        using var workspace = new TemporaryWorkspace();
        var directory = workspace.CreateDirectory("output");
        var paths = Enumerable.Range(1, count).Select(i => workspace.CreatePdf($"output/{i}.pdf")).ToArray();
        var store = new MemoryPreferencesStore(new("Dark", directory));
        var clock = new RecentTimeProvider();
        using var vm = CreateViewModel(new FakePdfOperationsClient
        {
            SplitHandler = (_, _) => Task.FromResult(OperationExecutionResult.Succeeded(paths)),
        }, preferencesStore: store, timeProvider: clock);
        SelectPdfToolMode(vm, PdfToolMode.Split);
        SelectPdfSplitMode(vm, PdfSplitMode.EveryPage);
        vm.PdfSplitOutputAsZip = false;
        vm.AddPaths([workspace.CreatePdf("source.pdf")]);
        await vm.WaitForPdfPreflightAsync();
        await vm.StartProcessingCommand.ExecuteAsync(null);
        var item = Assert.Single(vm.RecentOutputs);
        Assert.Equal(count == 1 ? paths[0] : directory, item.Path);
        Assert.Equal(count == 1 ? "1.pdf" : "1.pdf 等 2 个", item.DisplayName);
        Assert.Equal(count > 1, item.IsFolder);
        Assert.Equal(clock.GetUtcNow(), item.CreatedAt);
        Assert.True(vm.HasRecentOutputs);
        Assert.Equal("Dark", store.Load().Theme);
        Assert.Equal(directory, store.Load().LastOutputDirectory);
        Assert.Equal(item.Path, Assert.Single(store.Load().RecentOutputs!).Path);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecentOutputs_DeduplicateMoveToFrontAndLimitToTwenty(bool duplicate)
    {
        using var workspace = new TemporaryWorkspace();
        var directory = workspace.CreateDirectory("output");
        var clock = new RecentTimeProvider();
        var output = Path.Combine(directory, "merged.pdf");
        var saved = Enumerable.Range(0, 20).Select(i => new RecentOutput(
            duplicate && i == 10 ? output.ToUpperInvariant() : Path.Combine(directory, $"{i}.pdf"),
            $"{i}.pdf", clock.GetUtcNow().AddDays(-i - 1))).Reverse().ToArray();
        var store = new MemoryPreferencesStore(new("Light", directory, saved));
        using var vm = CreateViewModel(new FakePdfOperationsClient
        {
            MergeHandler = (_, _) => Task.FromResult(OperationExecutionResult.Succeeded([output])),
        }, preferencesStore: store, timeProvider: clock);
        Assert.Equal("0.pdf", vm.RecentOutputs[0].DisplayName);
        vm.AddPaths([workspace.CreatePdf("a.pdf"), workspace.CreatePdf("b.pdf")]);
        await vm.WaitForPdfPreflightAsync();
        await vm.StartProcessingCommand.ExecuteAsync(null);
        Assert.Equal(20, vm.RecentOutputs.Count);
        Assert.Equal(output, vm.RecentOutputs[0].Path);
        Assert.Single(vm.RecentOutputs, item => string.Equals(item.Path, output, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(duplicate ? "19.pdf" : "18.pdf", vm.RecentOutputs[^1].DisplayName);
        Assert.Equal(vm.RecentOutputs.Select(item => item.Path), store.Load().RecentOutputs!.Select(item => item.Path));
    }

    [Fact]
    public async Task RecentOutputs_RenameDoesNotRecord()
    {
        using var workspace = new TemporaryWorkspace();
        var store = new MemoryPreferencesStore(new());
        using var vm = new WorkspaceViewModel(new FakeFilePickerService(), new FakeOfficeWorkerClient(),
            new FakeShellService(), batchRenameExecutor: new RecentRenameExecutor(), preferencesStore: store);
        vm.SelectedTool = vm.Tools.Single(tool => tool.Operation == DocumentOperation.BatchRename);
        vm.RenameSearchText = "old";
        vm.RenameReplacementText = "new";
        vm.AddPaths([workspace.CreatePdf("old.pdf")]);
        await vm.StartProcessingCommand.ExecuteAsync(null);
        Assert.True(vm.HasCompletedResult);
        Assert.Empty(vm.RecentOutputs);
        Assert.Null(store.Load().RecentOutputs);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RecentOutputs_MissingTargetIsRemovedAndPersisted(bool folder, bool reveal)
    {
        using var workspace = new TemporaryWorkspace();
        var path = Path.Combine(workspace.CreateDirectory("output"), "missing");
        var store = new MemoryPreferencesStore(new(RecentOutputs: [new(path, "missing", DateTimeOffset.UtcNow, folder)]));
        var shell = new FakeShellService();
        using var vm = CreateViewModel(new FakePdfOperationsClient(), shell, preferencesStore: store);
        var notified = false;
        vm.PropertyChanged += (_, args) => notified |= args.PropertyName == nameof(vm.HasRecentOutputs);
        var item = Assert.Single(vm.RecentOutputs);
        if (reveal) vm.RevealRecentOutputCommand.Execute(item);
        else vm.OpenRecentOutputCommand.Execute(item);
        Assert.Empty(vm.RecentOutputs);
        Assert.Empty(store.Load().RecentOutputs!);
        Assert.False(vm.HasRecentOutputs);
        Assert.True(notified);
        Assert.Equal("文件已不存在：missing", vm.StatusMessage);
        Assert.Null(shell.OpenedFile);
        Assert.Null(shell.OpenedFolder);
        Assert.Null(shell.RevealedFile);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecentOutputs_OpenRevealAndRemove(bool folder)
    {
        using var workspace = new TemporaryWorkspace();
        var path = folder ? workspace.CreateDirectory("output") : workspace.CreatePdf("result.pdf");
        var store = new MemoryPreferencesStore(new(RecentOutputs: [new(path, "result", DateTimeOffset.UtcNow, folder)]));
        var shell = new FakeShellService();
        using var vm = CreateViewModel(new FakePdfOperationsClient(), shell, preferencesStore: store);
        var item = Assert.Single(vm.RecentOutputs);
        Assert.Equal(path, item.ToolTip);
        vm.OpenRecentOutputCommand.Execute(item);
        Assert.Equal(path, folder ? shell.OpenedFolder : shell.OpenedFile);
        vm.RevealRecentOutputCommand.Execute(item);
        Assert.Equal(path, folder ? shell.OpenedFolder : shell.RevealedFile);
        vm.RemoveRecentOutputCommand.Execute(item);
        Assert.Empty(store.Load().RecentOutputs!);
        Assert.True(folder ? Directory.Exists(path) : File.Exists(path));
    }

    [Theory]
    [InlineData(0, "刚刚")]
    [InlineData(59, "刚刚")]
    [InlineData(60, "1 分钟")]
    [InlineData(3599, "59 分钟")]
    [InlineData(3600, "1 小时")]
    [InlineData(86399, "23 小时")]
    [InlineData(86400, "昨天")]
    [InlineData(172800, "2 天")]
    [InlineData(518400, "6 天")]
    [InlineData(604800, "10月29日")]
    public void RecentOutputs_RelativeTimeThresholds(int seconds, string expected)
    {
        var clock = new RecentTimeProvider();
        var item = new RecentOutputItemViewModel(new("result.pdf", "result.pdf", clock.GetUtcNow().AddSeconds(-seconds)), clock);
        Assert.Equal(expected, item.RelativeTimeText);
    }

    [Fact]
    public void RecentOutputs_RefreshNotifiesAndUsesInjectedLocalDate()
    {
        var clock = new RecentTimeProvider();
        var store = new MemoryPreferencesStore(new(RecentOutputs:
            [new("result.pdf", "result.pdf", clock.GetUtcNow())]));
        using var vm = CreateViewModel(new FakePdfOperationsClient(), preferencesStore: store, timeProvider: clock);
        var item = Assert.Single(vm.RecentOutputs);
        var notified = false;
        item.PropertyChanged += (_, args) => notified |= args.PropertyName == nameof(item.RelativeTimeText);
        clock.Now = clock.Now.AddMinutes(2);
        vm.RefreshRecentOutputTimes();
        Assert.Equal("2 分钟", item.RelativeTimeText);
        Assert.True(notified);
        clock.Now = new DateTimeOffset(2026, 11, 5, 17, 0, 0, TimeSpan.Zero);
        var localDateItem = new RecentOutputItemViewModel(new("x", "x",
            new DateTimeOffset(2026, 11, 4, 16, 30, 0, TimeSpan.Zero)), clock);
        Assert.Equal("昨天", localDateItem.RelativeTimeText);
    }

    [Fact]
    public void RecentOutputs_WithoutStoreStartsEmpty()
    {
        using var vm = CreateViewModel(new FakePdfOperationsClient());
        Assert.Empty(vm.RecentOutputs);
        Assert.False(vm.HasRecentOutputs);
    }

    private sealed class RecentTimeProvider : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 5, 4, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone { get; } =
            TimeZoneInfo.CreateCustomTimeZone("Test UTC+8", TimeSpan.FromHours(8), "Test UTC+8", "Test UTC+8");
    }

    private sealed class RecentRenameExecutor : IBatchRenameExecutor
    {
        public IReadOnlyList<string> FindPendingTransactions() => [];
        public Task<OperationExecutionResult> ExecuteAsync(BatchRenameExecutionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(OperationExecutionResult.Succeeded(request.Items.Select(item => item.TargetPath).ToArray()));
        public Task<OperationExecutionResult> RecoverAsync(string journalPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<OperationExecutionResult> UndoAsync(string manifestPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
