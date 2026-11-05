using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocPivot.Infrastructure.Preferences;

namespace DocPivot.App.ViewModels;

public partial class WorkspaceViewModel
{
    private readonly IUserPreferencesStore? _preferencesStore;
    private readonly TimeProvider _timeProvider;
    private readonly SynchronizationContext? _interactionContext;
    private Removal? _undoableRemoval;
    private ITimer? _removalTimer;
    private readonly List<string> _completedOutputPaths = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoRemoveCommand))]
    private bool _hasUndoableRemoval;

    [ObservableProperty]
    private string _undoableRemovalText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RevealCompletedResultCommand))]
    private bool _hasCompletedResult;

    [ObservableProperty]
    private string _completedResultTitle = string.Empty;

    /// <summary>"合并结果.pdf · 4.9 MB · 桌面" — what was made and where, without a path.</summary>
    [ObservableProperty]
    private string _completedResultDetail = string.Empty;

    public bool CanOpenCompletedFile => HasCompletedResult && _completedOutputPaths.Count == 1;

    private void RememberRemoval(QueuedFileViewModel file, ObservableCollection<QueuedFileViewModel> queue, int index)
    {
        ClearUndoableRemoval();
        var removal = new Removal(file, queue, index);
        _undoableRemoval = removal;
        UndoableRemovalText = $"已移除 {file.FileName}";
        HasUndoableRemoval = true;
        _removalTimer = _timeProvider.CreateTimer(_ =>
        {
            void Expire()
            {
                if (ReferenceEquals(_undoableRemoval, removal))
                {
                    ClearUndoableRemoval();
                }
            }
            if (_interactionContext is null) Expire();
            else _interactionContext.Post(_ => Expire(), null);
        }, null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
    }

    private void ClearUndoableRemoval()
    {
        _removalTimer?.Dispose();
        _removalTimer = null;
        _undoableRemoval = null;
        HasUndoableRemoval = false;
        UndoableRemovalText = string.Empty;
    }

    private bool CanUndoRemove() => HasUndoableRemoval && !IsProcessing;

    [RelayCommand(CanExecute = nameof(CanUndoRemove))]
    private void UndoRemove()
    {
        if (!CanUndoRemove()) return;
        var removal = _undoableRemoval!;
        ClearUndoableRemoval();
        removal.Queue.Insert(Math.Min(removal.Index, removal.Queue.Count), removal.File);
        if (removal.File.IsPdfPreflightLoading) StartPdfPreflight(removal.File);
        if (removal.File.IsWorksheetSelectionLoading || removal.File.IsExcelToolPreflightLoading)
        {
            StartWorksheetDiscovery(removal.File);
        }
        StatusMessage = $"已恢复 {removal.File.FileName}";
    }

    private void ClearCompletedResult()
    {
        HasCompletedResult = false;
        CompletedResultTitle = string.Empty;
        CompletedResultDetail = string.Empty;
        _completedOutputPaths.Clear();
        NotifyCompletedFileChanged();
    }

    private void PublishCompletedResult(ProcessingSummary summary)
    {
        if (_completedOutputPaths.Count == 0) return;
        var count = _completedOutputPaths.Count;
        CompletedResultTitle = IsBatchRenameTool ? $"已重命名 {count} 个文件"
            : IsPdfMergeMode || IsExcelMergeMode ? "已合并"
            : IsPdfSplitMode && PdfSplitOutputAsZip ? "已拆分并打包"
            : IsPdfSplitMode || IsExcelOperationsTool && SelectedExcelToolMode.Mode == ExcelToolMode.Split
                ? $"已拆分为 {count} 个文件"
            : IsPdfCompressMode || IsExcelCompressMode ? $"已压缩 {count} 个文件"
            : IsPdfToExcelTool ? $"已提取 {count} 个文件"
            : $"已转换 {count} 个文件";
        if (summary.Failed > 0) CompletedResultTitle += $"，{summary.Failed} 个失败";
        CompletedResultDetail = DescribeCompletedOutputs();
        HasCompletedResult = true;
        NotifyCompletedFileChanged();
        RecordRecentOutputs();
    }

    private string DescribeCompletedOutputs()
    {
        var first = _completedOutputPaths[0];
        var folder = DescribeFolder(Path.GetDirectoryName(first));
        long bytes = 0;
        foreach (var path in _completedOutputPaths)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists) bytes += info.Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        var size = bytes > 0 ? $" · {FormatBytes(bytes)}" : string.Empty;
        return _completedOutputPaths.Count == 1
            ? $"{Path.GetFileName(first)}{size} · {folder}"
            : $"{_completedOutputPaths.Count} 个文件{size} · {folder}";
    }

    private void NotifyCompletedFileChanged()
    {
        OnPropertyChanged(nameof(CanOpenCompletedFile));
        OpenCompletedResultCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanOpenCompletedFile))]
    private void OpenCompletedResult()
    {
        if (CanOpenCompletedFile) _shellService.OpenFile(_completedOutputPaths[0]);
    }

    [RelayCommand(CanExecute = nameof(HasCompletedResult))]
    private void RevealCompletedResult()
    {
        if (!HasCompletedResult) return;
        if (CanOpenCompletedFile) _shellService.RevealInFolder(_completedOutputPaths[0]);
        else _shellService.OpenFolder(Path.GetDirectoryName(_completedOutputPaths[0])!);
    }

    private sealed record Removal(QueuedFileViewModel File, ObservableCollection<QueuedFileViewModel> Queue, int Index);
}
