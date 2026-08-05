using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocPivot.Core.Documents;
using DocPivot.Core.Jobs;
using DocPivot.Core.Renaming;
using DocPivot.Infrastructure.Operations;
using DocPivot.Infrastructure.Renaming;
using System.IO;

namespace DocPivot.App.ViewModels;

public partial class WorkspaceViewModel
{
    private IBatchRenameExecutor? _batchRenameExecutor;
    private BatchRenamePlan _currentRenamePlan = new([]);
    private bool _isRefreshingRenamePreview;
    private int _nextRenameAddedOrder;

    [ObservableProperty]
    private RenameToolModeOption _selectedRenameToolMode = null!;

    [ObservableProperty]
    private string _renameSearchText = string.Empty;

    [ObservableProperty]
    private string _renameReplacementText = string.Empty;

    [ObservableProperty]
    private bool _renameMatchCase;

    [ObservableProperty]
    private bool _renameReplaceAll = true;

    [ObservableProperty]
    private bool _renameUseRegularExpression;

    [ObservableProperty]
    private string _renameInsertText = string.Empty;

    [ObservableProperty]
    private RenamePositionOption _selectedRenameInsertPosition = null!;

    [ObservableProperty]
    private int _renameInsertIndex;

    [ObservableProperty]
    private bool _renameInsertFromEnd;

    [ObservableProperty]
    private bool _renameNumberingEnabled;

    [ObservableProperty]
    private long _renameNumberingStart = 1;

    [ObservableProperty]
    private long _renameNumberingStep = 1;

    [ObservableProperty]
    private int _renameNumberingPadding;

    [ObservableProperty]
    private string _renameNumberingPrefix = string.Empty;

    [ObservableProperty]
    private string _renameNumberingSuffix = string.Empty;

    [ObservableProperty]
    private RenamePositionOption _selectedRenameNumberingPosition = null!;

    [ObservableProperty]
    private int _renameNumberingIndex;

    [ObservableProperty]
    private bool _renameNumberingFromEnd;

    [ObservableProperty]
    private RenameSortModeOption _selectedRenameSortMode = null!;

    [ObservableProperty]
    private bool _renameIncludeExtension;

    [ObservableProperty]
    private bool _renameIncludeSubdirectories = true;

    [ObservableProperty]
    private string _renameManualNamesText = string.Empty;

    [ObservableProperty]
    private string? _lastRenameManifestPath;

    public IReadOnlyList<RenameToolModeOption> RenameToolModes { get; private set; } = [];

    public IReadOnlyList<RenamePositionOption> RenamePositions { get; private set; } = [];

    public IReadOnlyList<RenameSortModeOption> RenameSortModes { get; private set; } = [];

    public bool IsBatchRenameTool => SelectedTool.Operation == DocumentOperation.BatchRename;

    public bool UsesOutputDirectory => !IsBatchRenameTool;

    public bool IsRenameSearchMode =>
        IsBatchRenameTool && SelectedRenameToolMode.Mode == RenameToolMode.SearchReplace;

    public bool IsRenameInsertMode =>
        IsBatchRenameTool && SelectedRenameToolMode.Mode == RenameToolMode.Insert;

    public bool IsRenameNumberingMode =>
        IsBatchRenameTool && SelectedRenameToolMode.Mode == RenameToolMode.Numbering;

    public bool IsRenameInsertSpecifiedPosition =>
        IsRenameInsertMode &&
        SelectedRenameInsertPosition.Position == RenameInsertPosition.SpecifiedIndex;

    public bool IsRenameNumberingSpecifiedPosition =>
        IsRenameNumberingMode &&
        SelectedRenameNumberingPosition.Position == RenameInsertPosition.SpecifiedIndex;

    public int RenameChangedCount => _currentRenamePlan.ChangedCount;

    public int RenameConflictCount => _currentRenamePlan.ConflictCount;

    public int RenameInvalidCount => _currentRenamePlan.InvalidCount;

    public string RenamePreviewSummary =>
        $"{RenameChangedCount} 将变更 · {RenameConflictCount} 冲突 · {RenameInvalidCount} 无效";

    public string QueueSectionTitle => IsBatchRenameTool ? "重命名预览" : "处理队列";

    public string DropZoneTitle => IsBatchRenameTool ? "拖放文件或文件夹" : "拖放文件";

    public bool HasRenameUndo => !string.IsNullOrWhiteSpace(LastRenameManifestPath);

    private void InitializeRenameOptions(IBatchRenameExecutor? executor)
    {
        _batchRenameExecutor = executor;
        RenameToolModes =
        [
            new(RenameToolMode.SearchReplace, "替换"),
            new(RenameToolMode.Insert, "插入"),
            new(RenameToolMode.Numbering, "编号"),
        ];
        SelectedRenameToolMode = RenameToolModes[0];
        RenamePositions =
        [
            new(RenameInsertPosition.Beginning, "开头"),
            new(RenameInsertPosition.SpecifiedIndex, "指定"),
            new(RenameInsertPosition.End, "末尾"),
        ];
        SelectedRenameInsertPosition = RenamePositions[0];
        SelectedRenameNumberingPosition = RenamePositions[^1];
        RenameSortModes =
        [
            new(RenameSortMode.AddedOrder, "加入顺序"),
            new(RenameSortMode.FileName, "原文件名"),
            new(RenameSortMode.CreationTime, "创建时间"),
            new(RenameSortMode.LastWriteTime, "修改时间"),
        ];
        SelectedRenameSortMode = RenameSortModes[0];
    }

    private IEnumerable<string> ExpandInputPaths(IEnumerable<string> paths)
    {
        if (!IsBatchRenameTool)
        {
            return paths;
        }

        var expanded = new List<string>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = RenameIncludeSubdirectories,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };
        foreach (var path in paths.Where(static path => !string.IsNullOrWhiteSpace(path)))
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(path);
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                continue;
            }

            if (!Directory.Exists(fullPath))
            {
                expanded.Add(fullPath);
                continue;
            }

            try
            {
                expanded.AddRange(Directory
                    .EnumerateFiles(fullPath, "*", options)
                    .Where(file => DocumentAdmissionPolicy.IsSupported(
                        DocumentOperation.BatchRename,
                        Path.GetExtension(file)))
                    .Take(DocumentLimits.MaximumRenameBatchFiles));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Inaccessible folders are skipped while other dropped paths continue.
            }
        }

        return expanded;
    }

    private void RefreshRenamePreview()
    {
        if (!IsBatchRenameTool || _isRefreshingRenamePreview)
        {
            return;
        }

        _isRefreshingRenamePreview = true;
        try
        {
            var sources = Files.Select(static file => new RenameSourceFile(
                file.FullPath,
                file.FileName,
                file.SizeBytes,
                file.CreationTimeUtc,
                file.LastWriteTimeUtc,
                file.RenameAddedOrder)).ToArray();
            var manualOverrides = Files
                .Where(static file => file.RenameManualOverride is not null)
                .ToDictionary(
                    static file => file.FullPath,
                    static file => file.RenameManualOverride!,
                    StringComparer.OrdinalIgnoreCase);
            _currentRenamePlan = BatchRenamePlanner.Create(
                sources,
                CreateRenameRuleSet(),
                manualOverrides,
                static path => File.Exists(path) || Directory.Exists(path));

            for (var targetIndex = 0; targetIndex < _currentRenamePlan.Items.Count; targetIndex++)
            {
                var item = _currentRenamePlan.Items[targetIndex];
                var file = Files.First(candidate => string.Equals(
                    candidate.FullPath,
                    item.Source.FullPath,
                    StringComparison.OrdinalIgnoreCase));
                var currentIndex = Files.IndexOf(file);
                if (currentIndex != targetIndex)
                {
                    Files.Move(currentIndex, targetIndex);
                }

                file.ApplyRenamePlanItem(item, GetRenamePlanStatusText(item));
            }
        }
        finally
        {
            _isRefreshingRenamePreview = false;
        }

        NotifyRenamePreviewChanged();
    }

    private BatchRenameRuleSet CreateRenameRuleSet() => new(
        new RenameSearchReplaceRule(
            RenameSearchText,
            RenameReplacementText,
            RenameMatchCase,
            RenameReplaceAll,
            RenameUseRegularExpression),
        new RenameInsertionRule(
            RenameInsertText,
            SelectedRenameInsertPosition.Position,
            RenameInsertIndex,
            RenameInsertFromEnd),
        new RenameNumberingRule(
            RenameNumberingEnabled,
            RenameNumberingStart,
            RenameNumberingStep,
            RenameNumberingPadding,
            RenameNumberingPrefix,
            RenameNumberingSuffix,
            SelectedRenameNumberingPosition.Position,
            RenameNumberingIndex,
            RenameNumberingFromEnd),
        SelectedRenameSortMode.Mode,
        RenameIncludeExtension);

    private void NotifyRenamePreviewChanged()
    {
        OnPropertyChanged(nameof(RenameChangedCount));
        OnPropertyChanged(nameof(RenameConflictCount));
        OnPropertyChanged(nameof(RenameInvalidCount));
        OnPropertyChanged(nameof(RenamePreviewSummary));
        OnPropertyChanged(nameof(PrimaryActionText));
        StartProcessingCommand.NotifyCanExecuteChanged();
        ApplyRenameManualNamesCommand.NotifyCanExecuteChanged();
        ClearAllRenameOverridesCommand.NotifyCanExecuteChanged();
        ClearRenameOverrideCommand.NotifyCanExecuteChanged();
        UpdateEngineStatus();
    }

    private void NotifyRenameToolSelectionChanged()
    {
        OnPropertyChanged(nameof(IsBatchRenameTool));
        OnPropertyChanged(nameof(UsesOutputDirectory));
        OnPropertyChanged(nameof(IsRenameSearchMode));
        OnPropertyChanged(nameof(IsRenameInsertMode));
        OnPropertyChanged(nameof(IsRenameNumberingMode));
        OnPropertyChanged(nameof(IsRenameInsertSpecifiedPosition));
        OnPropertyChanged(nameof(IsRenameNumberingSpecifiedPosition));
        OnPropertyChanged(nameof(HasRenameUndo));
        OnPropertyChanged(nameof(QueueSectionTitle));
        OnPropertyChanged(nameof(DropZoneTitle));
        AddRenameFolderCommand.NotifyCanExecuteChanged();
        UndoLastRenameCommand.NotifyCanExecuteChanged();
        RefreshRenamePreview();
    }

    private void NotifyRenameCommandsChanged()
    {
        AddRenameFolderCommand.NotifyCanExecuteChanged();
        ApplyRenameManualNamesCommand.NotifyCanExecuteChanged();
        ClearAllRenameOverridesCommand.NotifyCanExecuteChanged();
        ClearRenameOverrideCommand.NotifyCanExecuteChanged();
        UndoLastRenameCommand.NotifyCanExecuteChanged();
    }

    private bool CanStartBatchRename() =>
        _batchRenameExecutor is not null &&
        _currentRenamePlan.CanExecute &&
        Files.Any(static file => file.State == JobState.Queued);

    private async Task<ProcessingSummary> ProcessBatchRenameAsync(
        CancellationToken cancellationToken)
    {
        RefreshRenamePreview();
        if (_batchRenameExecutor is null || !_currentRenamePlan.CanExecute)
        {
            return new ProcessingSummary(0, 0, false, "重命名预览包含冲突或没有可执行变更");
        }

        var changedItems = _currentRenamePlan.Items
            .Where(static item => item.IsChanged)
            .ToArray();
        var filesByPath = Files.ToDictionary(
            static file => file.FullPath,
            StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files.Where(static file => file.State == JobState.Queued))
        {
            file.MarkValidating();
            file.MarkRunning("正在重命名");
        }

        EngineStatusTitle = "正在批量重命名";
        EngineStatusDetail = $"两阶段安全事务 · {changedItems.Length} 个文件";
        try
        {
            var result = await _batchRenameExecutor.ExecuteAsync(
                new BatchRenameExecutionRequest(changedItems.Select(static item =>
                    new BatchRenameExecutionItem(
                        item.Source.FullPath,
                        item.TargetPath,
                        item.Source.SizeBytes,
                        item.Source.LastWriteTimeUtc)).ToArray()),
                cancellationToken);
            if (result.IsSucceeded)
            {
                foreach (var item in _currentRenamePlan.Items)
                {
                    var file = filesByPath[item.Source.FullPath];
                    file.MarkSucceeded(
                        item.IsChanged ? item.TargetPath : item.Source.FullPath,
                        item.IsChanged
                            ? $"已重命名为 {item.ProposedFileName}"
                            : "名称未变化");
                }

                LastRenameManifestPath = result.Metrics.TryGetValue("manifestPath", out var manifestPath)
                    ? manifestPath
                    : null;
                var notice = result.Notices.Count > 0
                    ? GetRenameNotice(result.Notices[^1])
                    : null;
                return new ProcessingSummary(changedItems.Length, 0, false, notice);
            }

            var message = GetRenameExecutionError(result.ErrorCode);
            foreach (var file in Files.Where(static file => file.IsProcessing))
            {
                file.MarkFailed(result.ErrorCode ?? "RENAME_FAILED", message);
            }

            var recoveryNotice = result.Metrics.TryGetValue("recoveryReportPath", out var recoveryPath)
                ? $"回滚不完整，恢复报告：{recoveryPath}"
                : null;
            return new ProcessingSummary(0, changedItems.Length, false, recoveryNotice);
        }
        catch (OperationCanceledException)
        {
            foreach (var file in Files.Where(static file => file.IsProcessing))
            {
                file.MarkCanceled();
            }

            return new ProcessingSummary(0, 0, true, null);
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddRenameFolder))]
    private void AddRenameFolder()
    {
        var initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (_filePicker.PickRenameFolder(initialDirectory) is { } folder)
        {
            AddPaths([folder]);
        }
    }

    private bool CanAddRenameFolder() => IsBatchRenameTool && !IsProcessing;

    [RelayCommand(CanExecute = nameof(CanApplyRenameManualNames))]
    private void ApplyRenameManualNames()
    {
        var normalizedText = RenameManualNamesText.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var names = normalizedText.Split('\n').ToList();
        if (names.Count > 1 && names[^1].Length == 0)
        {
            names.RemoveAt(names.Count - 1);
        }

        _isRefreshingRenamePreview = true;
        try
        {
            for (var index = 0; index < Math.Min(names.Count, Files.Count); index++)
            {
                Files[index].RenamePreviewName = names[index];
            }
        }
        finally
        {
            _isRefreshingRenamePreview = false;
        }

        RefreshRenamePreview();
        StatusMessage = names.Count == Files.Count
            ? $"已应用 {names.Count} 个手动名称"
            : $"已应用 {Math.Min(names.Count, Files.Count)} 个名称；行数与文件数不一致";
    }

    private bool CanApplyRenameManualNames() =>
        IsBatchRenameTool && !IsProcessing && HasFiles && !string.IsNullOrEmpty(RenameManualNamesText);

    [RelayCommand(CanExecute = nameof(CanClearAllRenameOverrides))]
    private void ClearAllRenameOverrides()
    {
        foreach (var file in Files)
        {
            file.ClearRenameManualOverride();
        }

        RenameManualNamesText = string.Empty;
        RefreshRenamePreview();
        StatusMessage = "已清除全部手动名称";
    }

    private bool CanClearAllRenameOverrides() =>
        IsBatchRenameTool && !IsProcessing && Files.Any(static file => file.IsRenameManuallyEdited);

    [RelayCommand(CanExecute = nameof(CanClearRenameOverride))]
    private void ClearRenameOverride(QueuedFileViewModel? file)
    {
        file?.ClearRenameManualOverride();
        RefreshRenamePreview();
    }

    private bool CanClearRenameOverride(QueuedFileViewModel? file) =>
        IsBatchRenameTool && !IsProcessing && file?.CanEditRenamePreview == true &&
        file.IsRenameManuallyEdited;

    [RelayCommand(CanExecute = nameof(CanUndoLastRename))]
    private async Task UndoLastRenameAsync()
    {
        if (_batchRenameExecutor is null || LastRenameManifestPath is null)
        {
            return;
        }

        IsProcessing = true;
        try
        {
            var result = await _batchRenameExecutor.UndoAsync(
                LastRenameManifestPath,
                _lifetimeCancellation.Token);
            if (result.IsSucceeded)
            {
                foreach (var file in Files)
                {
                    file.MarkRenameUndoSucceeded();
                }

                LastRenameManifestPath = null;
                StatusMessage = $"已撤销重命名：恢复 {result.Artifacts.Count} 个文件";
            }
            else
            {
                StatusMessage = GetRenameExecutionError(result.ErrorCode);
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "撤销已取消";
        }
        finally
        {
            IsProcessing = false;
            UpdateEngineStatus();
        }
    }

    private bool CanUndoLastRename() =>
        IsBatchRenameTool && !IsProcessing && _batchRenameExecutor is not null && HasRenameUndo;

    public Task WaitForRenameUndoCompletionAsync() =>
        UndoLastRenameCommand.ExecutionTask ?? Task.CompletedTask;

    private static string GetRenamePlanStatusText(RenamePlanItem item) => item.Status switch
    {
        RenamePlanItemStatus.Ready when item.HasManualOverride => "手动名称有效",
        RenamePlanItemStatus.Ready => "将重命名",
        RenamePlanItemStatus.Unchanged => "名称未变化",
        _ => GetRenameValidationError(item.ErrorCode),
    };

    private static string GetRenameValidationError(string? code) => code switch
    {
        "RENAME_NAME_EMPTY" => "名称不能为空",
        "RENAME_NAME_TOO_LONG" => "文件名超过 255 个字符",
        "RENAME_NAME_TRAILING_CHARACTER" => "名称不能以空格或句点结尾",
        "RENAME_NAME_INVALID_CHARACTER" => "名称包含 Windows 非法字符",
        "RENAME_NAME_RESERVED" => "名称是 Windows 保留名",
        "RENAME_EXTENSION_PROTECTED" => "扩展名受保护；需显式允许修改",
        "RENAME_EXTENSION_UNSUPPORTED" => "扩展名不在支持范围内",
        "RENAME_PATH_TOO_LONG" => "完整路径过长",
        "RENAME_TARGET_DUPLICATE" => "批次内目标名称重复",
        "RENAME_TARGET_EXISTS" => "目标文件已存在",
        "RENAME_TARGET_UNAVAILABLE" => "无法检查目标路径",
        "RENAME_REGEX_INVALID" => "正则表达式无效",
        "RENAME_REGEX_TIMEOUT" => "正则表达式执行超时",
        "RENAME_RULE_TOO_LONG" => "规则内容过长",
        "RENAME_POSITION_INVALID" => "插入位置无效",
        "RENAME_POSITION_OUT_OF_RANGE" => "指定位置超出名称长度",
        "RENAME_NUMBERING_INVALID" => "编号起始值、步长或位数无效",
        "RENAME_TOKEN_UNKNOWN" => "包含未知令牌",
        "RENAME_DATE_FORMAT_INVALID" => "日期令牌格式无效",
        _ => "名称校验失败",
    };

    private static string GetRenameExecutionError(string? code) => code switch
    {
        "RENAME_SOURCE_MISSING" => "源文件已被移动或删除，请重新添加。",
        "RENAME_SOURCE_CHANGED" => "预览后源文件已被修改，已拒绝执行。",
        "RENAME_TARGET_EXISTS" => "目标名称已被占用，请刷新预览。",
        "RENAME_UNDO_SOURCE_CHANGED" => "重命名后的文件已被外部修改，不能自动撤销。",
        "RENAME_ROLLBACK_INCOMPLETE" => "重命名失败且回滚不完整，请按恢复报告处理。",
        "RENAME_MANIFEST_WRITE_FAILED" => "无法保存撤销清单，重命名已回滚。",
        "RENAME_MANIFEST_INVALID" or "RENAME_MANIFEST_UNAVAILABLE" => "撤销清单无效或不可读取。",
        "RENAME_IO_FAILURE" => "重命名失败，已恢复全部源文件。",
        _ => "批量重命名失败，源文件未被覆盖。",
    };

    private static string GetRenameNotice(DocPivot.Core.Contracts.WorkerNotice notice) =>
        notice.Code switch
        {
            "RENAME_UNDO_AVAILABLE" => "已保存本地撤销清单",
            "RENAME_UNDO_COMPLETED" => "已恢复原文件名",
            _ => notice.Message,
        };

    partial void OnSelectedRenameToolModeChanged(RenameToolModeOption value)
    {
        OnPropertyChanged(nameof(IsRenameSearchMode));
        OnPropertyChanged(nameof(IsRenameInsertMode));
        OnPropertyChanged(nameof(IsRenameNumberingMode));
        OnPropertyChanged(nameof(IsRenameInsertSpecifiedPosition));
        OnPropertyChanged(nameof(IsRenameNumberingSpecifiedPosition));
    }

    partial void OnSelectedRenameInsertPositionChanged(RenamePositionOption value)
    {
        OnPropertyChanged(nameof(IsRenameInsertSpecifiedPosition));
        RefreshRenamePreview();
    }

    partial void OnSelectedRenameNumberingPositionChanged(RenamePositionOption value)
    {
        OnPropertyChanged(nameof(IsRenameNumberingSpecifiedPosition));
        RefreshRenamePreview();
    }

    partial void OnSelectedRenameSortModeChanged(RenameSortModeOption value) => RefreshRenamePreview();

    partial void OnRenameSearchTextChanged(string value) => RefreshRenamePreview();

    partial void OnRenameReplacementTextChanged(string value) => RefreshRenamePreview();

    partial void OnRenameMatchCaseChanged(bool value) => RefreshRenamePreview();

    partial void OnRenameReplaceAllChanged(bool value) => RefreshRenamePreview();

    partial void OnRenameUseRegularExpressionChanged(bool value) => RefreshRenamePreview();

    partial void OnRenameInsertTextChanged(string value) => RefreshRenamePreview();

    partial void OnRenameInsertIndexChanged(int value) => RefreshRenamePreview();

    partial void OnRenameInsertFromEndChanged(bool value) => RefreshRenamePreview();

    partial void OnRenameNumberingEnabledChanged(bool value) => RefreshRenamePreview();

    partial void OnRenameNumberingStartChanged(long value) => RefreshRenamePreview();

    partial void OnRenameNumberingStepChanged(long value) => RefreshRenamePreview();

    partial void OnRenameNumberingPaddingChanged(int value) => RefreshRenamePreview();

    partial void OnRenameNumberingPrefixChanged(string value) => RefreshRenamePreview();

    partial void OnRenameNumberingSuffixChanged(string value) => RefreshRenamePreview();

    partial void OnRenameNumberingIndexChanged(int value) => RefreshRenamePreview();

    partial void OnRenameNumberingFromEndChanged(bool value) => RefreshRenamePreview();

    partial void OnRenameIncludeExtensionChanged(bool value) => RefreshRenamePreview();

    partial void OnRenameManualNamesTextChanged(string value) =>
        ApplyRenameManualNamesCommand.NotifyCanExecuteChanged();

    partial void OnLastRenameManifestPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasRenameUndo));
        UndoLastRenameCommand.NotifyCanExecuteChanged();
    }
}
