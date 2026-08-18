using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocPivot.App.Services;
using DocPivot.Core.Contracts;
using DocPivot.Core.Documents;
using DocPivot.Core.Excel;
using DocPivot.Core.Jobs;
using DocPivot.Core.Pdf;
using DocPivot.Infrastructure.Office;
using DocPivot.Infrastructure.Operations;
using DocPivot.Infrastructure.Pdf;
using DocPivot.Infrastructure.Pdf.Ghostscript;
using DocPivot.Infrastructure.Renaming;
using DocPivot.Infrastructure.Storage;
using DocPivot.Infrastructure.Tables;

namespace DocPivot.App.ViewModels;

public partial class WorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly IFilePickerService _filePicker;
    private readonly IOfficeWorkerClient _officeWorkerClient;
    private readonly IExcelOperationsClient? _excelOperationsClient;
    private readonly IPdfOperationsClient? _pdfOperationsClient;
    private readonly IPdfTableOperationsClient? _pdfTableOperationsClient;
    private readonly IPdfThumbnailRenderer? _pdfThumbnailRenderer;
    private readonly IShellService _shellService;
    private readonly Dictionary<DocumentOperation, ObservableCollection<QueuedFileViewModel>> _queues;
    private readonly Dictionary<PdfToolMode, ObservableCollection<QueuedFileViewModel>> _pdfModeQueues;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private Task _initializationTask = Task.CompletedTask;
    private bool _isInitialized;
    private bool _isDisposed;

    [ObservableProperty]
    private ToolNavigationItem _selectedTool;

    [ObservableProperty]
    private string _outputDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    [ObservableProperty]
    private string _statusMessage = "就绪";

    [ObservableProperty]
    private bool _isEngineReady;

    [ObservableProperty]
    private bool _isProcessing;

    [ObservableProperty]
    private ExcelToolModeOption _selectedExcelToolMode;

    [ObservableProperty]
    private ExcelImageCompressionProfileOption _selectedExcelImageCompressionProfile;

    [ObservableProperty]
    private PdfToolModeOption _selectedPdfToolMode;

    [ObservableProperty]
    private PdfSplitModeOption _selectedPdfSplitMode;

    [ObservableProperty]
    private PdfOcrModeOption _selectedPdfOcrMode;

    [ObservableProperty]
    private bool _pdfCombineIntoOneWorksheet;

    public WorkspaceViewModel(
        IFilePickerService filePicker,
        IOfficeWorkerClient officeWorkerClient,
        IShellService shellService,
        IExcelOperationsClient? excelOperationsClient = null,
        IPdfOperationsClient? pdfOperationsClient = null,
        IBatchRenameExecutor? batchRenameExecutor = null,
        IPdfTableOperationsClient? pdfTableOperationsClient = null,
        IPdfThumbnailRenderer? pdfThumbnailRenderer = null)
    {
        _filePicker = filePicker;
        _officeWorkerClient = officeWorkerClient;
        _excelOperationsClient = excelOperationsClient;
        _pdfOperationsClient = pdfOperationsClient;
        _pdfTableOperationsClient = pdfTableOperationsClient;
        _pdfThumbnailRenderer = pdfThumbnailRenderer;
        _shellService = shellService;
        Settings = new SettingsViewModel(_officeWorkerClient);
        Tools =
        [
            new(DocumentOperation.OfficeToPdf, "Office 转 PDF", "调用本机 Office 原生导出", "M4.226 20.925A2 2 0 0 0 6 22h12a2 2 0 0 0 2-2V8a2.4 2.4 0 0 0-.706-1.706l-3.588-3.588A2.4 2.4 0 0 0 14 2H6a2 2 0 0 0-2 2v3.127M14 2v5a1 1 0 0 0 1 1h5M5 11l-3 3M5 17l-3-3h10", "DOC · DOCX · XLS · XLSX"),
            new(DocumentOperation.PdfToExcel, "PDF 转 Excel", "提取电子版与扫描表格", "M6 22a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h8a2.4 2.4 0 0 1 1.704.706l3.588 3.588A2.4 2.4 0 0 1 20 8v12a2 2 0 0 1-2 2zM14 2v5a1 1 0 0 0 1 1h5M8 13h2M14 13h2M8 17h2M14 17h2", "PDF"),
            new(DocumentOperation.ExcelOperations, "Excel 工具", "工作表合并、拆分与压缩", "M3 3h18v18H3zM3 9h18M9 3v18M15 9v12", "XLS · XLSX"),
            new(DocumentOperation.PdfOperations, "PDF 工具", "页面合并、拆分与压缩", "M15 2h-4a2 2 0 0 0-2 2v11a2 2 0 0 0 2 2h8a2 2 0 0 0 2-2V8M16.706 2.706A2.4 2.4 0 0 0 15 2v5a1 1 0 0 0 1 1h5a2.4 2.4 0 0 0-.706-1.706zM5 7a2 2 0 0 0-2 2v11a2 2 0 0 0 2 2h8a2 2 0 0 0 1.732-1", "PDF"),
            new(DocumentOperation.BatchRename, "批量重命名", "预览、校验与可撤销命名", "M14.364 13.634a2 2 0 0 0-.506.854l-.837 2.87a.5.5 0 0 0 .62.62l2.87-.837a2 2 0 0 0 .854-.506l4.013-4.009a1 1 0 0 0-3.004-3.004zM14.487 7.858A1 1 0 0 1 14 7V2M20 19.645V20a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h8a2.4 2.4 0 0 1 1.704.706l2.516 2.516M8 18h1", "DOC · DOCX · XLS · XLSX · PDF"),
        ];

        _selectedTool = Tools[0];
        ExcelToolModes =
        [
            new(ExcelToolMode.Merge, "合并", "按队列顺序汇总所有工作表"),
            new(ExcelToolMode.Split, "拆分", "每张工作表输出一个独立文件"),
            new(ExcelToolMode.Compress, "压缩", "安全清理数据边界外的冗余行列"),
        ];
        _selectedExcelToolMode = ExcelToolModes[0];
        ExcelImageCompressionProfiles =
        [
            new(ExcelImageCompressionLevel.None, "不处理", "保留嵌入图片原始像素与编码"),
            new(ExcelImageCompressionLevel.HighQuality, "清晰", "保持像素尺寸，JPEG 质量 90"),
            new(ExcelImageCompressionLevel.Balanced, "平衡", "图片缩放至 75%，JPEG 质量 82"),
            new(ExcelImageCompressionLevel.SmallFile, "小体积", "图片缩放至 50%，JPEG 质量 72"),
        ];
        _selectedExcelImageCompressionProfile = ExcelImageCompressionProfiles[0];
        PdfToolModes =
        [
            new(PdfToolMode.Merge, "合并", "按队列顺序合并页面"),
            new(PdfToolMode.Split, "拆分", "按页码规则生成新文件"),
            new(PdfToolMode.Compress, "压缩", "无损优化或图片重采样"),
        ];
        _selectedPdfToolMode = PdfToolModes[0];
        PdfMergeOutputName = "merged";
        PdfSplitModes =
        [
            new(PdfSplitMode.VisualCuts, "可视化"),
            new(PdfSplitMode.CustomRange, "页码范围"),
            new(PdfSplitMode.EveryNPages, "每 N 页"),
            new(PdfSplitMode.EveryPage, "逐页"),
            new(PdfSplitMode.OddPages, "奇数页"),
            new(PdfSplitMode.EvenPages, "偶数页"),
        ];
        _selectedPdfSplitMode = PdfSplitModes[0];
        PdfOcrModes =
        [
            new(WorkerPdfOcrMode.Automatic, "智能", "优先文字层；内容不足时自动使用本地 OCR"),
            new(WorkerPdfOcrMode.DigitalTextOnly, "仅文字", "只读取 PDF 可搜索文字层，不渲染页面"),
            new(WorkerPdfOcrMode.ForceOcr, "强制 OCR", "所有页面均在本机渲染并重新识别"),
        ];
        _selectedPdfOcrMode = PdfOcrModes[0];
        _pdfCombineIntoOneWorksheet = true;
        PdfPageThumbnails = [];
        InitializeRenameOptions(batchRenameExecutor);
        _queues = Tools.ToDictionary(
            static tool => tool.Operation,
            static _ => new ObservableCollection<QueuedFileViewModel>());
        _pdfModeQueues = PdfToolModes.ToDictionary(
            static mode => mode.Mode,
            static _ => new ObservableCollection<QueuedFileViewModel>());

        foreach (var queue in _queues.Values.Concat(_pdfModeQueues.Values))
        {
            queue.CollectionChanged += OnQueueChanged;
        }
    }

    public IReadOnlyList<ToolNavigationItem> Tools { get; }

    public IReadOnlyList<ExcelToolModeOption> ExcelToolModes { get; }

    public IReadOnlyList<ExcelImageCompressionProfileOption> ExcelImageCompressionProfiles { get; }

    public IReadOnlyList<PdfToolModeOption> PdfToolModes { get; }

    public IReadOnlyList<PdfSplitModeOption> PdfSplitModes { get; }

    public IReadOnlyList<PdfOcrModeOption> PdfOcrModes { get; }

    public ObservableCollection<QueuedFileViewModel> Files =>
        IsPdfOperationsTool
            ? _pdfModeQueues[SelectedPdfToolMode.Mode]
            : _queues[SelectedTool.Operation];

    public bool HasFiles => Files.Count > 0;

    public bool HasPendingFiles => Files.Any(static file => file.State == JobState.Queued);

    public bool HasResults => Files.Any(static file => file.CanOpenResult);

    public bool IsNotProcessing => !IsProcessing;

    public bool IsOfficeTool => SelectedTool.Operation == DocumentOperation.OfficeToPdf;

    public bool IsPdfToExcelTool => SelectedTool.Operation == DocumentOperation.PdfToExcel;

    public bool IsExcelOperationsTool => SelectedTool.Operation == DocumentOperation.ExcelOperations;

    public bool IsExcelMergeMode =>
        IsExcelOperationsTool && SelectedExcelToolMode.Mode == ExcelToolMode.Merge;

    public bool IsExcelCompressMode =>
        IsExcelOperationsTool && SelectedExcelToolMode.Mode == ExcelToolMode.Compress;

    public string ExcelImageCompressionDetail => SelectedExcelImageCompressionProfile.Description;

    public bool IsPdfOperationsTool => SelectedTool.Operation == DocumentOperation.PdfOperations;

    public bool IsAnyPdfTool => IsPdfToExcelTool || IsPdfOperationsTool;

    public bool IsPdfMergeMode =>
        IsPdfOperationsTool && SelectedPdfToolMode.Mode == PdfToolMode.Merge;

    public bool IsPdfSplitMode =>
        IsPdfOperationsTool && SelectedPdfToolMode.Mode == PdfToolMode.Split;

    public bool IsPdfCompressMode =>
        IsPdfOperationsTool && SelectedPdfToolMode.Mode == PdfToolMode.Compress;

    public bool IsPdfCustomRangeMode =>
        IsPdfSplitMode && SelectedPdfSplitMode.Mode == PdfSplitMode.CustomRange;

    public bool IsPdfEveryNPagesMode =>
        IsPdfSplitMode && SelectedPdfSplitMode.Mode == PdfSplitMode.EveryNPages;

    public bool IsPdfVisualSplitMode =>
        IsPdfSplitMode && SelectedPdfSplitMode.Mode == PdfSplitMode.VisualCuts;

    public bool ShowPdfMergeOutputName => IsPdfMergeMode;

    public bool IsMergeOrderingEnabled => IsExcelMergeMode || IsPdfMergeMode;

    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorkspaceVisible))]
    private bool _isSettingsOpen;

    public bool IsWorkspaceVisible => !IsSettingsOpen;

    [RelayCommand]
    private void OpenSettings()
    {
        IsSettingsOpen = true;
        _ = Settings.EnsureInitialProbeAsync();
    }

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;

    public bool IsSelectedToolReady => SelectedTool.Operation switch
    {
        DocumentOperation.OfficeToPdf => IsEngineReady,
        DocumentOperation.PdfToExcel => IsPdfTableEngineReadyForSelection(),
        DocumentOperation.ExcelOperations =>
            _excelOperationsClient is not null && _officeEngineStatus?.IsExcelReady == true,
        DocumentOperation.PdfOperations => IsPdfEngineReadyForSelection(),
        DocumentOperation.BatchRename => _batchRenameExecutor is not null,
        _ => false,
    };

    public string QueueCountText => $"{Files.Count} / {QueueMaximum}";

    private int QueueMaximum => IsPdfSplitMode
        ? 1
        : IsBatchRenameTool
            ? DocumentLimits.MaximumRenameBatchFiles
            : DocumentLimits.MaximumBatchFiles;

    public string PrimaryActionText
    {
        get
        {
            var count = Files.Count(static file => file.State == JobState.Queued);
            return SelectedTool.Operation switch
            {
                DocumentOperation.ExcelOperations when SelectedExcelToolMode.Mode == ExcelToolMode.Merge =>
                    count == 0 ? "开始合并" : $"合并 {count} 个工作簿",
                DocumentOperation.ExcelOperations when SelectedExcelToolMode.Mode == ExcelToolMode.Split =>
                    count == 0 ? "开始拆分" : $"拆分 {count} 个工作簿",
                DocumentOperation.ExcelOperations =>
                    count == 0 ? "开始压缩" : $"压缩 {count} 个工作簿",
                DocumentOperation.PdfOperations when SelectedPdfToolMode.Mode == PdfToolMode.Merge =>
                    count == 0 ? "开始合并" : $"合并 {count} 个 PDF",
                DocumentOperation.PdfOperations when SelectedPdfToolMode.Mode == PdfToolMode.Split =>
                    count == 0 ? "开始拆分" : $"拆分 {count} 个 PDF",
                DocumentOperation.PdfOperations =>
                    count == 0 ? "开始压缩" : $"压缩 {count} 个 PDF",
                DocumentOperation.PdfToExcel =>
                    count == 0 ? "开始提取" : $"提取 {count} 个 PDF",
                DocumentOperation.BatchRename =>
                    RenameChangedCount == 0 ? "执行重命名" : $"重命名 {RenameChangedCount} 个文件",
                _ => count == 0 ? "开始转换" : $"转换 {count} 个文件",
            };
        }
    }

    public Task InitializeAsync()
    {
        if (_isDisposed)
        {
            return Task.CompletedTask;
        }

        if (_isInitialized)
        {
            return _initializationTask;
        }

        _isInitialized = true;
        _initializationTask = InitializeCoreAsync();
        return _initializationTask;
    }

    private async Task InitializeCoreAsync()
    {
        var lifetimeToken = _lifetimeCancellation.Token;
        var pdfProbeTask = _pdfOperationsClient is IPdfEngineStatusProvider pdfEngineStatusProvider
            ? pdfEngineStatusProvider.ProbeEnginesAsync(lifetimeToken)
            : null;
        var pdfTableProbeTask = _pdfTableOperationsClient?.ProbeAsync(lifetimeToken);
        try
        {
            _officeEngineStatus = await _officeWorkerClient.ProbeAsync(lifetimeToken);
            lifetimeToken.ThrowIfCancellationRequested();
            IsEngineReady = _officeEngineStatus.IsReady;
            UpdateEngineStatus();
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _officeEngineStatus = new OfficeEngineStatus(
                false,
                null,
                null,
                null,
                "无法启动 Office 处理引擎。");
            IsEngineReady = false;
            UpdateEngineStatus();
        }

        if (pdfProbeTask is not null)
        {
            try
            {
                _pdfEngineStatus = await pdfProbeTask;
                lifetimeToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _pdfEngineStatus = null;
                _pdfEngineProbeError = "无法验证本地 PDF 处理引擎。";
            }

            OnPropertyChanged(nameof(IsSelectedToolReady));
            UpdateEngineStatus();
        }

        if (pdfTableProbeTask is not null)
        {
            try
            {
                _pdfTableEngineStatus = await pdfTableProbeTask;
                lifetimeToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _pdfTableEngineStatus = PdfTableEngineStatus.Unavailable(
                    "PDF_TABLE_WORKER_UNAVAILABLE",
                    "无法启动 PDF 表格提取引擎。");
            }

            OnPropertyChanged(nameof(IsSelectedToolReady));
            UpdateEngineStatus();
        }

        StartProcessingCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanAddFiles))]
    private void AddFiles()
    {
        AddPaths(_filePicker.PickFiles(SelectedTool.Operation));
    }

    private bool CanAddFiles() => !IsProcessing;

    public void AddPaths(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (IsProcessing)
        {
            StatusMessage = "任务处理中，暂时不能添加文件";
            return;
        }

        var added = 0;
        var skipped = 0;

        foreach (var path in ExpandInputPaths(paths)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            QueuedFileViewModel? queuedFile;
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (Files.Any(file => string.Equals(file.FullPath, fullPath, StringComparison.OrdinalIgnoreCase)))
                {
                    skipped++;
                    continue;
                }

                var info = new FileInfo(fullPath);
                if (!info.Exists)
                {
                    skipped++;
                    continue;
                }

                var admission = DocumentAdmissionPolicy.Evaluate(
                    SelectedTool.Operation,
                    info.Extension,
                    info.Length,
                    Files.Count);

                queuedFile = admission.IsAccepted
                    ? new QueuedFileViewModel(
                        info.FullName,
                        info.Name,
                        info.Extension,
                        info.Length,
                        SelectedTool.Operation == DocumentOperation.OfficeToPdf,
                        SelectedTool.Operation is
                            DocumentOperation.PdfOperations or DocumentOperation.PdfToExcel,
                        SelectedTool.Operation == DocumentOperation.BatchRename,
                        info.CreationTimeUtc,
                        info.LastWriteTimeUtc,
                        SelectedTool.Operation == DocumentOperation.BatchRename
                            ? _nextRenameAddedOrder++
                            : 0,
                        SelectedTool.Operation == DocumentOperation.ExcelOperations,
                        blockSignedPdfChanges: false)
                    : null;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or
                System.Security.SecurityException)
            {
                skipped++;
                continue;
            }

            if (queuedFile is null)
            {
                skipped++;
                continue;
            }

            if (IsPdfSplitMode && Files.Count >= 1)
            {
                skipped++;
                StatusMessage = "PDF 拆分一次只能处理一个文件";
                continue;
            }

            Files.Add(queuedFile);
            StartWorksheetDiscovery(queuedFile);
            StartPdfPreflight(queuedFile);
            added++;
        }

        StatusMessage = (added, skipped) switch
        {
            (> 0, 0) => $"已添加 {added} 个文件",
            (> 0, > 0) => $"已添加 {added} 个文件，跳过 {skipped} 个",
            _ => "没有可添加的文件",
        };
    }

    public void ReportImportFailure()
    {
        StatusMessage = "无法读取拖入的文件，请检查路径和访问权限";
    }

    [RelayCommand(CanExecute = nameof(CanRemoveFile))]
    private void RemoveFile(QueuedFileViewModel? file)
    {
        if (file is null)
        {
            return;
        }

        if (!Files.Remove(file))
        {
            return;
        }

        StatusMessage = "已从队列移除文件";
    }

    private bool CanRemoveFile(QueuedFileViewModel? file) => file is not null && !IsProcessing;

    [RelayCommand(CanExecute = nameof(CanMoveFileUp))]
    private void MoveFileUp(QueuedFileViewModel? file)
    {
        if (file is null)
        {
            return;
        }

        var index = Files.IndexOf(file);
        if (index <= 0)
        {
            return;
        }

        Files.Move(index, index - 1);
        NotifyMoveCommandsChanged();
    }

    private bool CanMoveFileUp(QueuedFileViewModel? file) =>
        file is not null && IsMergeOrderingEnabled && !IsProcessing && Files.IndexOf(file) > 0;

    [RelayCommand(CanExecute = nameof(CanMoveFileDown))]
    private void MoveFileDown(QueuedFileViewModel? file)
    {
        if (file is null)
        {
            return;
        }

        var index = Files.IndexOf(file);
        if (index < 0 || index >= Files.Count - 1)
        {
            return;
        }

        Files.Move(index, index + 1);
        NotifyMoveCommandsChanged();
    }

    private bool CanMoveFileDown(QueuedFileViewModel? file) =>
        file is not null && IsMergeOrderingEnabled && !IsProcessing &&
        Files.IndexOf(file) is var index && index >= 0 && index < Files.Count - 1;

    public bool MoveFileBefore(QueuedFileViewModel? source, QueuedFileViewModel? target)
    {
        if (target is null)
        {
            return false;
        }

        var targetIndex = Files.IndexOf(target);
        return targetIndex >= 0 && MoveFileTo(source, targetIndex);
    }

    public bool MoveFileTo(QueuedFileViewModel? source, int insertIndex)
    {
        if (source is null || !IsMergeOrderingEnabled || IsProcessing)
        {
            return false;
        }

        var sourceIndex = Files.IndexOf(source);
        if (sourceIndex < 0)
        {
            return false;
        }

        // The insert index is expressed against the collection that still
        // contains the dragged item, so it shifts by one when moving downwards.
        var clampedIndex = Math.Clamp(insertIndex, 0, Files.Count);
        var destinationIndex = clampedIndex > sourceIndex ? clampedIndex - 1 : clampedIndex;
        if (destinationIndex == sourceIndex)
        {
            return false;
        }

        Files.Move(sourceIndex, destinationIndex);
        NotifyMoveCommandsChanged();
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanClearQueue))]
    private void ClearQueue()
    {
        foreach (var file in Files)
        {
            CancelWorksheetDiscovery(file);
            CancelPdfPreflight(file);
        }

        Files.Clear();
        StatusMessage = "队列已清空";
    }

    private bool CanClearQueue() => HasFiles && !IsProcessing;

    [RelayCommand(CanExecute = nameof(CanChooseOutputDirectory))]
    private void ChooseOutputDirectory()
    {
        if (_filePicker.PickFolder(OutputDirectory) is { } selectedDirectory)
        {
            OutputDirectory = selectedDirectory;
            StatusMessage = "已更新输出目录";
        }
    }

    private bool CanChooseOutputDirectory() => !IsProcessing;

    private static void RunOnUiThread(Action action)
    {
        // Preview refreshes can be triggered from threadpool continuations (for
        // example preflight completion), but bound ObservableCollections only
        // accept writes on the dispatcher thread. Test hosts may leak a foreign
        // or shut-down Application.Current dispatcher, in which case invoking
        // is impossible and the action runs inline instead.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess() || dispatcher.HasShutdownStarted)
        {
            action();
            return;
        }

        try
        {
            dispatcher.Invoke(action);
        }
        catch (TaskCanceledException)
        {
            action();
        }
    }

    private static bool TryGetMetricInt64(
        IReadOnlyDictionary<string, string> metrics,
        string key,
        out long value)
    {
        value = 0;
        return metrics.TryGetValue(key, out var text) &&
            long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static string FormatByteCount(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)Math.Max(bytes, 0);
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? $"{bytes.ToString(CultureInfo.InvariantCulture)} {units[unitIndex]}"
            : $"{value.ToString("0.##", CultureInfo.InvariantCulture)} {units[unitIndex]}";
    }

    public void RequestShutdown()
    {
        if (_isDisposed)
        {
            return;
        }

        if (!_lifetimeCancellation.IsCancellationRequested)
        {
            _lifetimeCancellation.Cancel();
        }

        CancelProcessing();
        _pdfThumbnailCancellation?.Cancel();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        RequestShutdown();
        _isDisposed = true;
        PdfPageThumbnails.Clear();
        if (_pdfThumbnailDirectory is { } previewDirectory)
        {
            if (_pdfThumbnailTask.IsCompleted)
            {
                TryDeletePreviewDirectory(previewDirectory);
            }
            else
            {
                _ = _pdfThumbnailTask.ContinueWith(
                    static (_, state) => TryDeletePreviewDirectory((string)state!),
                    previewDirectory,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        if (_initializationTask.IsCompleted)
        {
            _lifetimeCancellation.Dispose();
        }
        else
        {
            _ = _initializationTask.ContinueWith(
                static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                _lifetimeCancellation,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        GC.SuppressFinalize(this);
    }

    [RelayCommand]
    private void OpenResult(QueuedFileViewModel? file)
    {
        if (file?.OutputPath is null ||
            (!File.Exists(file.OutputPath) && !Directory.Exists(file.OutputPath)))
        {
            StatusMessage = "结果文件不存在或已被移动";
            return;
        }

        try
        {
            if (Directory.Exists(file.OutputPath))
            {
                _shellService.OpenFolder(file.OutputPath);
            }
            else
            {
                _shellService.OpenFile(file.OutputPath);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StatusMessage = "无法打开结果文件，请从输出目录手动打开";
        }
    }

    [RelayCommand]
    private void OpenOutputDirectory()
    {
        if (!Directory.Exists(OutputDirectory))
        {
            StatusMessage = "输出目录不存在";
            return;
        }

        try
        {
            _shellService.OpenFolder(OutputDirectory);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StatusMessage = "无法打开输出目录";
        }
    }

    partial void OnSelectedToolChanged(ToolNavigationItem value)
    {
        OnPropertyChanged(nameof(Files));
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(HasPendingFiles));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(IsOfficeTool));
        OnPropertyChanged(nameof(IsPdfToExcelTool));
        OnPropertyChanged(nameof(IsExcelOperationsTool));
        OnPropertyChanged(nameof(IsExcelMergeMode));
        OnPropertyChanged(nameof(IsExcelCompressMode));
        OnPropertyChanged(nameof(IsPdfOperationsTool));
        OnPropertyChanged(nameof(IsAnyPdfTool));
        OnPropertyChanged(nameof(IsPdfMergeMode));
        OnPropertyChanged(nameof(IsPdfSplitMode));
        OnPropertyChanged(nameof(IsPdfCompressMode));
        OnPropertyChanged(nameof(IsPdfCustomRangeMode));
        OnPropertyChanged(nameof(IsPdfEveryNPagesMode));
        OnPropertyChanged(nameof(IsPdfVisualSplitMode));
        OnPropertyChanged(nameof(ShowPdfVisualPreview));
        OnPropertyChanged(nameof(ShowPdfMergeOutputName));
        OnPropertyChanged(nameof(IsBatchRenameTool));
        OnPropertyChanged(nameof(UsesOutputDirectory));
        OnPropertyChanged(nameof(IsMergeOrderingEnabled));
        OnPropertyChanged(nameof(IsSelectedToolReady));
        OnPropertyChanged(nameof(QueueCountText));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(ExcelMergePreviewSummary));
        OnPropertyChanged(nameof(ExcelMergePreviewDetail));
        ClearQueueCommand.NotifyCanExecuteChanged();
        StartProcessingCommand.NotifyCanExecuteChanged();
        NotifyMoveCommandsChanged();
        NotifyRenameToolSelectionChanged();
        StatusMessage = "就绪";
        RefreshPdfVisualPreview();
        UpdateEngineStatus();
        // 点击任一功能模块时回到该模块界面（关闭设置页）
        IsSettingsOpen = false;
    }

    partial void OnSelectedExcelToolModeChanged(ExcelToolModeOption value)
    {
        OnPropertyChanged(nameof(IsExcelMergeMode));
        OnPropertyChanged(nameof(IsExcelCompressMode));
        OnPropertyChanged(nameof(IsMergeOrderingEnabled));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(ExcelMergePreviewSummary));
        OnPropertyChanged(nameof(ExcelMergePreviewDetail));
        StartProcessingCommand.NotifyCanExecuteChanged();
        NotifyMoveCommandsChanged();
        UpdateEngineStatus();
    }

    partial void OnSelectedExcelImageCompressionProfileChanged(
        ExcelImageCompressionProfileOption value)
    {
        OnPropertyChanged(nameof(ExcelImageCompressionDetail));
    }

    partial void OnSelectedPdfToolModeChanged(PdfToolModeOption value)
    {
        OnPropertyChanged(nameof(Files));
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(HasPendingFiles));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(IsPdfMergeMode));
        OnPropertyChanged(nameof(IsPdfSplitMode));
        OnPropertyChanged(nameof(IsPdfCompressMode));
        OnPropertyChanged(nameof(IsPdfCustomRangeMode));
        OnPropertyChanged(nameof(IsPdfEveryNPagesMode));
        OnPropertyChanged(nameof(IsPdfVisualSplitMode));
        OnPropertyChanged(nameof(ShowPdfVisualPreview));
        OnPropertyChanged(nameof(ShowPdfMergeOutputName));
        OnPropertyChanged(nameof(QueueCountText));
        OnPropertyChanged(nameof(IsMergeOrderingEnabled));
        OnPropertyChanged(nameof(IsSelectedToolReady));
        OnPropertyChanged(nameof(PrimaryActionText));
        StartProcessingCommand.NotifyCanExecuteChanged();
        NotifyMoveCommandsChanged();
        RefreshPdfVisualPreview();
        UpdateEngineStatus();
    }

    partial void OnSelectedPdfOcrModeChanged(PdfOcrModeOption value)
    {
        OnPropertyChanged(nameof(IsSelectedToolReady));
        StartProcessingCommand.NotifyCanExecuteChanged();
        UpdateEngineStatus();
    }

    partial void OnPdfCombineIntoOneWorksheetChanged(bool value)
    {
        StartProcessingCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedPdfSplitModeChanged(PdfSplitModeOption value)
    {
        OnPropertyChanged(nameof(IsPdfCustomRangeMode));
        OnPropertyChanged(nameof(IsPdfEveryNPagesMode));
        OnPropertyChanged(nameof(IsPdfVisualSplitMode));
        OnPropertyChanged(nameof(ShowPdfVisualPreview));
        OnPropertyChanged(nameof(QueueCountText));
        StartProcessingCommand.NotifyCanExecuteChanged();
        RefreshPdfVisualPreview();
    }

    partial void OnPdfPageRangeChanged(string value)
    {
        StartProcessingCommand.NotifyCanExecuteChanged();
    }

    partial void OnPdfPagesPerFileChanged(int value)
    {
        StartProcessingCommand.NotifyCanExecuteChanged();
    }

    partial void OnPdfSplitOutputAsZipChanged(bool value)
    {
        StartProcessingCommand.NotifyCanExecuteChanged();
    }

    partial void OnPdfCompressionStrengthChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentPdfCompressionProfile));
        OnPropertyChanged(nameof(PdfCompressionDetail));
        OnPropertyChanged(nameof(PdfCompressionPreservationNote));
        OnPropertyChanged(nameof(IsSelectedToolReady));
        StartProcessingCommand.NotifyCanExecuteChanged();
        UpdateEngineStatus();
    }

    partial void OnOutputDirectoryChanged(string value)
    {
        StartProcessingCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsEngineReadyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSelectedToolReady));
        StartProcessingCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsProcessingChanged(bool value)
    {
        foreach (var queue in _queues.Values.Concat(_pdfModeQueues.Values))
        {
            foreach (var file in queue)
            {
                file.SetWorksheetSelectionLocked(value);
            }
        }

        OnPropertyChanged(nameof(IsNotProcessing));
        AddFilesCommand.NotifyCanExecuteChanged();
        RemoveFileCommand.NotifyCanExecuteChanged();
        ClearQueueCommand.NotifyCanExecuteChanged();
        ChooseOutputDirectoryCommand.NotifyCanExecuteChanged();
        StartProcessingCommand.NotifyCanExecuteChanged();
        CancelProcessingCommand.NotifyCanExecuteChanged();
        NotifyMoveCommandsChanged();
        NotifyRenameCommandsChanged();
        TogglePdfCutCommand.NotifyCanExecuteChanged();
        RotatePdfPageCommand.NotifyCanExecuteChanged();
        RotatePdfFileCommand.NotifyCanExecuteChanged();
    }

    private void OnQueueChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Move && e.OldItems is not null)
        {
            foreach (QueuedFileViewModel file in e.OldItems)
            {
                CancelWorksheetDiscovery(file);
                CancelPdfPreflight(file);
                file.PropertyChanged -= OnFilePropertyChanged;
            }
        }

        if (e.Action != NotifyCollectionChangedAction.Move && e.NewItems is not null)
        {
            foreach (QueuedFileViewModel file in e.NewItems)
            {
                file.PropertyChanged += OnFilePropertyChanged;
            }
        }

        if (ReferenceEquals(sender, Files))
        {
            NotifyQueuePropertiesChanged();
            RefreshRenamePreview();
            if (e.Action == NotifyCollectionChangedAction.Move && IsPdfMergeMode)
            {
                RefreshPdfVisualOrder();
            }
            else
            {
                RefreshPdfVisualPreview();
            }
        }
    }

    private void OnFilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueuedFileViewModel.RenameManualOverride) &&
            IsBatchRenameTool &&
            !_isRefreshingRenamePreview)
        {
            RefreshRenamePreview();
        }

        if (e.PropertyName is
            nameof(QueuedFileViewModel.State) or
            nameof(QueuedFileViewModel.OutputPath) or
            nameof(QueuedFileViewModel.WorksheetDiscoveryState) or
            nameof(QueuedFileViewModel.ExcelToolPreflightState) or
            nameof(QueuedFileViewModel.PdfPreflightState) or
            nameof(QueuedFileViewModel.HasPdfSignature))
        {
            NotifyQueuePropertiesChanged();
        }
    }

    private void NotifyQueuePropertiesChanged()
    {
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(HasPendingFiles));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(QueueCountText));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(ExcelMergePreviewSummary));
        OnPropertyChanged(nameof(ExcelMergePreviewDetail));
        ClearQueueCommand.NotifyCanExecuteChanged();
        StartProcessingCommand.NotifyCanExecuteChanged();
        NotifyMoveCommandsChanged();
    }

    private static string CreateUniqueOutputPath(
        string inputPath,
        string outputDirectory,
        HashSet<string> reservedPaths)
        => CreateUniqueOutputPath(inputPath, outputDirectory, reservedPaths, ".pdf", string.Empty);

    private static string CreateUniqueOutputPath(
        string inputPath,
        string outputDirectory,
        HashSet<string> reservedPaths,
        string extension,
        string nameSuffix)
    {
        var baseName = Path.GetFileNameWithoutExtension(inputPath);
        return CreateUniqueNamedOutputPath(
            outputDirectory,
            $"{baseName}{nameSuffix}",
            extension,
            reservedPaths);
    }

    private static string CreateUniqueNamedOutputPath(
        string outputDirectory,
        string baseName,
        string extension,
        HashSet<string>? reservedPaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        var normalizedExtension = extension.StartsWith('.') ? extension : $".{extension}";
        var candidate = Path.Combine(outputDirectory, $"{baseName}{normalizedExtension}");
        var suffix = 2;
        while (File.Exists(candidate) || Directory.Exists(candidate) || reservedPaths?.Contains(candidate) == true)
        {
            candidate = Path.Combine(outputDirectory, $"{baseName} ({suffix}){normalizedExtension}");
            suffix++;
        }

        reservedPaths?.Add(candidate);
        return candidate;
    }

    private static string CreateUniqueOutputDirectoryPath(
        string outputDirectory,
        string directoryName,
        HashSet<string> reservedPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryName);
        ArgumentNullException.ThrowIfNull(reservedPaths);

        var candidate = Path.Combine(outputDirectory, directoryName);
        var suffix = 2;
        while (File.Exists(candidate) || Directory.Exists(candidate) || reservedPaths.Contains(candidate))
        {
            candidate = Path.Combine(outputDirectory, $"{directoryName} ({suffix})");
            suffix++;
        }

        reservedPaths.Add(candidate);
        return candidate;
    }

    private static void TryDeleteEmptyDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Cleanup is best effort and must not replace the operation result.
        }
    }

    private void NotifyMoveCommandsChanged()
    {
        MoveFileUpCommand.NotifyCanExecuteChanged();
        MoveFileDownCommand.NotifyCanExecuteChanged();
    }

    private static string GetActionableError(string? errorCode, string? workerMessage) => errorCode switch
    {
        "INPUT_NOT_FOUND" => "源文件不存在或已被移动。",
        "INPUT_TOO_LARGE" => "文件超过 100 MB 上限。",
        "SIGNATURE_MISMATCH" or "OFFICE_BINARY_TYPE_MISMATCH" or "OFFICE_OPEN_XML_TYPE_MISMATCH" =>
            "文件扩展名与实际类型不匹配，或文件已损坏。",
        "WORD_NOT_INSTALLED" => "未检测到 Microsoft Word，请检查 Office 安装。",
        "EXCEL_NOT_INSTALLED" => "未检测到 Microsoft Excel，请检查 Office 安装。",
        "OUTPUT_ALREADY_EXISTS" => "输出文件已存在，请更换输出目录后重试。",
        "OUTPUT_ACCESS_DENIED" => "无法写入输出目录，请选择有写入权限的位置。",
        "OUTPUT_IO_FAILURE" => "输出写入失败，请检查磁盘空间和文件占用。",
        "OFFICE_TIMEOUT" => "Office 响应超时，请关闭可能存在的 Office 对话框后重试。",
        "OFFICE_COM_FAILURE" => "Office 导出失败，请先确认源文件能在 Office 中正常打开。",
        "EXCEL_WORKSHEET_NOT_FOUND" => "所选工作表已不存在，请重新添加文件并选择。",
        "EXCEL_WORKSHEET_HIDDEN" => "所选工作表已被隐藏，请重新选择可见工作表。",
        "EXCEL_WORKSHEET_TYPE_UNSUPPORTED" => "所选工作表类型暂不支持单独导出。",
        "EXCEL_WORKSHEET_NAME_INVALID" => "工作表名称无效，请重新读取工作表。",
        "INPUT_DUPLICATE" => "同一个工作簿不能在一次合并中重复出现。",
        "EXCEL_MERGE_REQUIRES_MULTIPLE_INPUTS" => "Excel 合并至少需要两个工作簿。",
        "EXCEL_VBA_UNSUPPORTED" => "工作簿包含 VBA 宏，当前不会在 XLSX 输出中静默丢弃，请先另存为无宏副本。",
        "EXCEL_CHART_SHEET_UNSUPPORTED" => "工作簿包含图表工作表，当前无法安全处理。",
        "EXCEL_DATE_SYSTEM_MISMATCH" => "工作簿使用不同的日期系统，合并可能改变日期值，已阻止处理。",
        "EXCEL_EXTERNAL_LINKS_BLOCKED" or
        "EXCEL_EXTERNAL_LINKS_PRESENT" or
        "EXCEL_EXTERNAL_LINKS_INTRODUCED" =>
            "工作簿包含或会产生外部链接，当前保留策略不允许继续。",
        "EXCEL_EXTERNAL_LINKS_NOT_PRESERVED" => "输出未完整保留外部链接，已取消提交。",
        "EXCEL_CROSS_SHEET_REFERENCE_UNSUPPORTED" or "EXCEL_SPLIT_CROSS_SHEET_REFERENCE" =>
            "拆分会产生指向原工作簿的链接；请启用“保留公式与外部链接”后重试。",
        "EXCEL_NO_WORKSHEETS_SELECTED" => "没有符合当前选项的工作表可处理。",
        "EXCEL_COMPRESSION_UNAUDITED_REFERENCE" or "EXCEL_COMPRESSION_UNSAFE" =>
            "有效数据边界外仍有对象或规则；请启用“风险工作表保持原样”后重试。",
        "EXCEL_CHART_SHEETS_NOT_PRESERVED" => "输出未完整保留图表工作表，已取消提交。",
        "PDF_PASSWORD_REQUIRED" => "PDF 已加密且需要打开密码，当前版本无法处理。",
        "PDF_PASSWORD_INVALID" => "PDF 密码不正确，当前版本无法处理该文件。",
        "PDF_SIGNATURE_PRESENT" => "PDF 包含数字签名，重写会使签名失效，已阻止处理。",
        "PDF_PAGE_LIMIT_EXCEEDED" => "PDF 超过 1000 页上限，或合并结果将超过上限。",
        "PDF_STRUCTURE_INVALID" or "PDF_METADATA_INVALID" or "PDF_OUTPUT_INVALID" =>
            "PDF 结构损坏或无法安全读取。",
        "PDF_OUTPUT_ALREADY_EXISTS" => "PDF 输出已存在，请重试以自动分配新名称。",
        "PDF_NO_COMPRESSION_BENEFIT" => "未发现可用压缩收益，已保留源内容。",
        "PDF_DECRYPTED_WITHOUT_COMPRESSION" => "PDF 已成功解锁，但无损重写未减小体积。",
        "PDF_TEXT_VERIFICATION_FAILED" => "无法验证压缩前后的可搜索文字，已取消输出。",
        "PDF_SEARCHABLE_TEXT_LOST" => "压缩会丢失可搜索文字，已取消输出。",
        "PDF_SEARCHABLE_TEXT_MISMATCH" => "压缩改变了可搜索文字内容，已取消输出。",
        "QPDF_RUNTIME_MISSING" or "QPDF_RUNTIME_HASH_MISMATCH" or "QPDF_VERSION_MISMATCH" =>
            "本地 qpdf 运行时缺失或校验失败，请重新安装文枢。",
        "GHOSTSCRIPT_RUNTIME_MISSING" or "GHOSTSCRIPT_RUNTIME_HASH_MISMATCH" or
            "GHOSTSCRIPT_VERSION_MISMATCH" =>
            "本地 Ghostscript 运行时缺失或校验失败，请重新安装文枢。",
        "QPDF_TIMEOUT" or "GHOSTSCRIPT_TIMEOUT" => "PDF 处理超时，请重试。",
        "PDF_ACCESS_DENIED" => "无法读取 PDF 或写入输出目录，请检查权限。",
        "PDF_IO_FAILURE" => "PDF 读写失败，请检查磁盘空间和文件占用。",
        "PDF_TABLE_NOT_FOUND" => "没有检测到可导出的表格或文字行，请切换识别方式后重试。",
        "PDF_TABLE_CELL_LIMIT_EXCEEDED" => "提取结果超过 100 万个单元格，请拆分 PDF 后重试。",
        "OCR_PAGE_TOO_LARGE" => "页面尺寸过大，无法在安全内存限制内执行 OCR。",
        "OCR_LANGUAGE_UNAVAILABLE" or "OCR_MODEL_RESOURCE_MISSING" or
        "OCR_MODEL_HASH_MISMATCH" or "OCR_ENGINE_UNAVAILABLE" =>
            "本地 OCR 模型缺失或校验失败，请重新安装文枢。",
        "PDF_PAGE_RENDER_FAILED" or "OCR_IMAGE_INVALID" => "扫描页渲染失败，请确认 PDF 可正常打开。",
        "XLSX_VALIDATION_FAILED" => "生成的 Excel 未通过完整性校验，已取消输出。",
        "PDF_SOURCE_CHANGED" => "处理期间源 PDF 被其他程序修改，请关闭后重试。",
        "PDF_TABLE_TIMEOUT" => "PDF 表格提取超时，请拆分文件后重试。",
        "PDF_TABLE_ACCESS_DENIED" => "无法读取 PDF、OCR 模型或写入输出目录，请检查权限。",
        "PDF_TABLE_IO_FAILURE" => "PDF 表格提取读写失败，请检查磁盘空间和文件占用。",
        "OUTPUT_DIRECTORY_INVALID" => "拆分输出位置不是有效目录。",
        "SOURCE_OVERWRITE_BLOCKED" => "输出不能覆盖源工作簿。",
        _ => string.IsNullOrWhiteSpace(workerMessage) ? "处理失败，请重试。" : workerMessage,
    };

    private sealed record ProcessingSummary(
        int Succeeded,
        int Failed,
        bool Canceled,
        string? Notice);
}
