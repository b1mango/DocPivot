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
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _worksheetDiscoveryGate = new(1, 1);
    private readonly object _worksheetDiscoverySync = new();
    private readonly Dictionary<QueuedFileViewModel, CancellationTokenSource> _worksheetDiscoveryCancellations = [];
    private readonly Dictionary<QueuedFileViewModel, Task> _worksheetDiscoveryTasks = [];
    private readonly SemaphoreSlim _pdfPreflightGate = new(2, 2);
    private readonly object _pdfPreflightSync = new();
    private readonly Dictionary<QueuedFileViewModel, CancellationTokenSource> _pdfPreflightCancellations = [];
    private readonly Dictionary<QueuedFileViewModel, Task> _pdfPreflightTasks = [];
    private Task _initializationTask = Task.CompletedTask;
    private Task _pdfThumbnailTask = Task.CompletedTask;
    private CancellationTokenSource? _pdfThumbnailCancellation;
    private string? _pdfThumbnailDirectory;
    private CancellationTokenSource? _processingCancellation;
    private OfficeEngineStatus? _officeEngineStatus;
    private PdfEngineStatus? _pdfEngineStatus;
    private PdfTableEngineStatus? _pdfTableEngineStatus;
    private string? _pdfEngineProbeError;
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
    private string _engineStatusTitle = "正在检测 Office";

    [ObservableProperty]
    private string _engineStatusDetail = "正在确认本机 Word 和 Excel 可用性";

    [ObservableProperty]
    private ExcelToolModeOption _selectedExcelToolMode;

    [ObservableProperty]
    private bool _includeHiddenWorksheets = true;

    [ObservableProperty]
    private bool _preserveExternalLinks = true;

    [ObservableProperty]
    private bool _skipUnsafeCompressionSheets = true;

    [ObservableProperty]
    private bool _convertLegacyWorkbookToOpenXml = true;

    [ObservableProperty]
    private ExcelImageCompressionProfileOption _selectedExcelImageCompressionProfile;

    [ObservableProperty]
    private PdfToolModeOption _selectedPdfToolMode;

    [ObservableProperty]
    private PdfSplitModeOption _selectedPdfSplitMode;

    [ObservableProperty]
    private string _pdfPageRange = "1-";

    [ObservableProperty]
    private int _pdfPagesPerFile = 10;

    [ObservableProperty]
    private int _pdfCompressionStrength;

    [ObservableProperty]
    private bool _pdfSplitOutputAsZip;

    [ObservableProperty]
    private string _pdfPreviewStatus = string.Empty;

    [ObservableProperty]
    private bool _isPdfPreviewLoading;

    [ObservableProperty]
    private PdfOcrModeOption _selectedPdfOcrMode;

    [ObservableProperty]
    private bool _pdfWorksheetPerPage;

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
        Tools =
        [
            new(DocumentOperation.OfficeToPdf, "Office 转 PDF", "调用本机 Office 原生导出", "M6,3 h8 q2,0 2,2 v13 q0,2 -2,2 h-8 q-2,0 -2,-2 v-13 q0,-2 2,-2 M8,7 h6 M8,10 h6 M8,13 h3 M16,17 l-3,4 l-2,-3", "DOC · DOCX · XLS · XLSX"),
            new(DocumentOperation.PdfToExcel, "PDF 转 Excel", "提取电子版与扫描表格", "M5,4 h14 q1,0 1,1 v12 q0,1 -1,1 h-14 q-1,0 -1,-1 v-12 q0,-1 1,-1 M5,9 h14 M12,4 v14", "PDF"),
            new(DocumentOperation.ExcelOperations, "Excel 工具", "工作表合并、拆分与压缩", "M5,3 h11 q2,0 3,3 v12 q0,3 -2,3 h-11 q-2,0 -3,-3 v-12 q0,-3 3,-3 M5,8 h14 M5,14 h14", "XLS · XLSX"),
            new(DocumentOperation.PdfOperations, "PDF 工具", "页面合并、拆分与压缩", "M7,5 h7 q2,0 2,2 v9 q0,2 -2,2 h-7 q-2,0 -2,-2 v-9 q0,-2 2,-2 M9,7 h7 q2,0 2,2 v9 q0,2 -2,2 h-7 q-2,0 -2,-2 v-9 q0,-2 2,-2 M11,9 h7 q2,0 2,2 v9 q0,2 -2,2 h-7 q-2,0 -2,-2 v-9 q0,-2 2,-2", "PDF"),
            new(DocumentOperation.BatchRename, "批量重命名", "预览、校验与可撤销命名", "M6,4 h8 q2,0 2,2 v10 q0,2 -2,2 h-8 q-2,0 -2,-2 v-10 q0,-2 2,-2 M8,8 h4 M8,11 h3 M17,17 l-4,-4", "DOC · DOCX · XLS · XLSX · PDF"),
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
        _pdfWorksheetPerPage = true;
        PdfPageThumbnails = [];
        InitializeRenameOptions(batchRenameExecutor);
        _queues = Tools.ToDictionary(
            static tool => tool.Operation,
            static _ => new ObservableCollection<QueuedFileViewModel>());

        foreach (var queue in _queues.Values)
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

    public ObservableCollection<PdfPageThumbnailViewModel> PdfPageThumbnails { get; }

    public ObservableCollection<QueuedFileViewModel> Files => _queues[SelectedTool.Operation];

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

    public string ExcelMergePreviewSummary => GetExcelMergePreview().Summary;

    public string ExcelMergePreviewDetail => GetExcelMergePreview().Detail;

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

    public bool ShowPdfVisualPreview =>
        IsPdfVisualSplitMode && PdfPageThumbnails.Count > 0;

    public string PdfVisualSplitSummary
    {
        get
        {
            var outputCount = PdfPageThumbnails.Count(static page => page.IsCutBefore) + 1;
            return $"{PdfPageThumbnails.Count} 页 · 将输出 {outputCount} 个 PDF";
        }
    }

    public bool IsMergeOrderingEnabled => IsExcelMergeMode || IsPdfMergeMode;

    public PdfCompressionProfile CurrentPdfCompressionProfile =>
        PdfCompressionProfile.FromStrength(PdfCompressionStrength);

    public string PdfCompressionDetail => CurrentPdfCompressionProfile.IsLossless
        ? "无损优化"
        : $"{CurrentPdfCompressionProfile.ImageDpi} DPI · JPEG {CurrentPdfCompressionProfile.JpegQuality}%";

    public string PdfCompressionPreservationNote => CurrentPdfCompressionProfile.IsLossless
        ? "不重采样图片；数字签名文件将对副本强制压缩，源文件保持原样。"
        : "线稿自动保留无损编码，扫描文字不低于 150 DPI；数字签名文件将对副本强制压缩，源文件保持原样。输出校验页数与可搜索文字层，批注、表单和附件不保证保留。";

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

    public string QueueCountText => $"{Files.Count} / {(IsBatchRenameTool ? DocumentLimits.MaximumRenameBatchFiles : DocumentLimits.MaximumBatchFiles)}";

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

    private void StartWorksheetDiscovery(QueuedFileViewModel file)
    {
        if ((!file.RequiresWorksheetSelection && !file.RequiresExcelToolPreflight) || _isDisposed)
        {
            return;
        }

        CancelWorksheetDiscovery(file);
        if (file.RequiresExcelToolPreflight)
        {
            file.BeginExcelToolPreflight();
        }
        else
        {
            file.BeginWorksheetDiscovery();
        }
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var task = LoadWorksheetOptionsAsync(file, cancellation.Token);
        lock (_worksheetDiscoverySync)
        {
            _worksheetDiscoveryCancellations[file] = cancellation;
            _worksheetDiscoveryTasks[file] = task;
        }

        _ = ObserveWorksheetDiscoveryAsync(file, task, cancellation);
    }

    private async Task LoadWorksheetOptionsAsync(
        QueuedFileViewModel file,
        CancellationToken cancellationToken)
    {
        var enteredGate = false;
        try
        {
            await _worksheetDiscoveryGate.WaitAsync(cancellationToken);
            enteredGate = true;
            var result = await _officeWorkerClient.ListExcelWorksheetsAsync(
                file.FullPath,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.IsSucceeded)
            {
                if (file.RequiresExcelToolPreflight)
                {
                    file.CompleteExcelToolPreflight(result.Worksheets, result.Compatibility);
                }
                else
                {
                    file.CompleteWorksheetDiscovery(result.Worksheets);
                }
            }
            else
            {
                FailExcelInspection(file, GetWorksheetDiscoveryError(result.ErrorCode));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A removed file or closing application no longer needs discovery results.
        }
        catch (TimeoutException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                FailExcelInspection(file, "读取 Excel 超时，请关闭 Excel 对话框后重试");
            }
        }
        catch (Exception)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                FailExcelInspection(file, "无法读取 Excel 工作簿，请重试");
            }
        }
        finally
        {
            if (enteredGate)
            {
                _worksheetDiscoveryGate.Release();
            }
        }
    }

    private async Task ObserveWorksheetDiscoveryAsync(
        QueuedFileViewModel file,
        Task task,
        CancellationTokenSource cancellation)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // LoadWorksheetOptionsAsync normally observes cancellation itself.
        }
        finally
        {
            lock (_worksheetDiscoverySync)
            {
                if (_worksheetDiscoveryCancellations.TryGetValue(file, out var currentCancellation) &&
                    ReferenceEquals(currentCancellation, cancellation))
                {
                    _worksheetDiscoveryCancellations.Remove(file);
                }

                if (_worksheetDiscoveryTasks.TryGetValue(file, out var currentTask) &&
                    ReferenceEquals(currentTask, task))
                {
                    _worksheetDiscoveryTasks.Remove(file);
                }
            }

            cancellation.Dispose();
        }
    }

    private void CancelWorksheetDiscovery(QueuedFileViewModel file)
    {
        CancellationTokenSource? cancellation;
        lock (_worksheetDiscoverySync)
        {
            _worksheetDiscoveryCancellations.TryGetValue(file, out cancellation);
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The completion observer already released this attempt.
        }
    }

    [RelayCommand]
    private void RetryWorksheetDiscovery(QueuedFileViewModel? file)
    {
        if (file is null || IsProcessing || !file.CanRetryWorksheetDiscovery)
        {
            return;
        }

        StartWorksheetDiscovery(file);
    }

    [RelayCommand]
    private void RetryExcelToolPreflight(QueuedFileViewModel? file)
    {
        if (file is null || IsProcessing || !file.CanRetryExcelToolPreflight)
        {
            return;
        }

        StartWorksheetDiscovery(file);
    }

    private static void FailExcelInspection(QueuedFileViewModel file, string message)
    {
        if (file.RequiresExcelToolPreflight)
        {
            file.FailExcelToolPreflight(message);
        }
        else
        {
            file.FailWorksheetDiscovery(message);
        }
    }

    [RelayCommand]
    private void ConfirmAllWorksheetsFallback(QueuedFileViewModel? file)
    {
        if (file is null || IsProcessing || !file.CanConfirmAllWorksheetsFallback)
        {
            return;
        }

        file.ConfirmAllWorksheetsFallback();
        StatusMessage = $"{file.FileName} 将导出全部工作表";
    }

    private void StartPdfPreflight(QueuedFileViewModel file)
    {
        if (!file.RequiresPdfPreflight || _isDisposed)
        {
            return;
        }

        CancelPdfPreflight(file);
        if (_pdfOperationsClient is null)
        {
            file.FailPdfPreflight("PDF 处理引擎尚未就绪", isEncrypted: false);
            return;
        }

        file.BeginPdfPreflight();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var task = LoadPdfPreflightAsync(file, cancellation.Token);
        lock (_pdfPreflightSync)
        {
            _pdfPreflightCancellations[file] = cancellation;
            _pdfPreflightTasks[file] = task;
        }

        _ = ObservePdfPreflightAsync(file, task, cancellation);
    }

    private async Task LoadPdfPreflightAsync(
        QueuedFileViewModel file,
        CancellationToken cancellationToken)
    {
        var enteredGate = false;
        try
        {
            await _pdfPreflightGate.WaitAsync(cancellationToken);
            enteredGate = true;
            var result = await _pdfOperationsClient!.PreflightAsync(
                new PdfPreflightRequest(
                    file.FullPath,
                    string.Empty),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.IsSucceeded && result.PageCount is { } pageCount)
            {
                file.CompletePdfPreflight(
                    pageCount,
                    result.HasSignatureFields,
                    result.IsEncrypted);
                RefreshPdfVisualPreview();
            }
            else
            {
                file.FailPdfPreflight(
                    GetPdfActionableError(result.ErrorCode, result.ErrorMessage),
                    result.IsEncrypted);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Removed files and application shutdown no longer need preflight results.
        }
        catch (TimeoutException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                file.FailPdfPreflight("PDF 检查超时，请重试", isEncrypted: false);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                file.FailPdfPreflight("无法检查 PDF，请确认文件可正常打开", isEncrypted: false);
            }
        }
        finally
        {
            if (enteredGate)
            {
                _pdfPreflightGate.Release();
            }
        }
    }

    private async Task ObservePdfPreflightAsync(
        QueuedFileViewModel file,
        Task task,
        CancellationTokenSource cancellation)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // LoadPdfPreflightAsync normally observes cancellation itself.
        }
        finally
        {
            lock (_pdfPreflightSync)
            {
                if (_pdfPreflightCancellations.TryGetValue(file, out var currentCancellation) &&
                    ReferenceEquals(currentCancellation, cancellation))
                {
                    _pdfPreflightCancellations.Remove(file);
                }

                if (_pdfPreflightTasks.TryGetValue(file, out var currentTask) &&
                    ReferenceEquals(currentTask, task))
                {
                    _pdfPreflightTasks.Remove(file);
                }
            }

            cancellation.Dispose();
        }
    }

    private void CancelPdfPreflight(QueuedFileViewModel file)
    {
        CancellationTokenSource? cancellation;
        lock (_pdfPreflightSync)
        {
            _pdfPreflightCancellations.TryGetValue(file, out cancellation);
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The completion observer already released this attempt.
        }
    }

    [RelayCommand]
    private void RetryPdfPreflight(QueuedFileViewModel? file)
    {
        if (file is null || IsProcessing || !file.CanRetryPdfPreflight)
        {
            return;
        }

        StartPdfPreflight(file);
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

    [RelayCommand(CanExecute = nameof(CanStartProcessing))]
    private async Task StartProcessingAsync()
    {
        var outputDirectory = string.Empty;
        if (!IsBatchRenameTool)
        {
            try
            {
                outputDirectory = Path.GetFullPath(OutputDirectory);
                Directory.CreateDirectory(outputDirectory);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or
                System.Security.SecurityException)
            {
                StatusMessage = "无法创建输出目录，请选择有写入权限的位置";
                return;
            }
        }

        var processingCancellation = new CancellationTokenSource();
        _processingCancellation = processingCancellation;
        IsProcessing = true;
        var cancellationToken = processingCancellation.Token;
        ProcessingSummary summary;

        try
        {
            if (IsOfficeTool)
            {
                summary = await ProcessOfficeConversionsAsync(outputDirectory, cancellationToken);
            }
            else if (IsPdfToExcelTool && _pdfTableOperationsClient is not null)
            {
                summary = await ProcessPdfToExcelAsync(outputDirectory, cancellationToken);
            }
            else if (IsExcelOperationsTool && _excelOperationsClient is not null)
            {
                summary = await ProcessExcelOperationsAsync(outputDirectory, cancellationToken);
            }
            else if (IsPdfOperationsTool && _pdfOperationsClient is not null)
            {
                summary = await ProcessPdfOperationsAsync(outputDirectory, cancellationToken);
            }
            else if (IsBatchRenameTool && _batchRenameExecutor is not null)
            {
                summary = await ProcessBatchRenameAsync(cancellationToken);
            }
            else
            {
                summary = new ProcessingSummary(0, 0, false, "处理引擎尚未就绪");
            }
        }
        catch (OperationCanceledException)
        {
            summary = new ProcessingSummary(0, 0, true, null);
        }
        finally
        {
            if (ReferenceEquals(_processingCancellation, processingCancellation))
            {
                _processingCancellation = null;
            }

            processingCancellation.Dispose();
            IsProcessing = false;
            UpdateEngineStatus();
        }

        var completion = summary.Canceled
            ? $"已取消：成功 {summary.Succeeded}，失败 {summary.Failed}"
            : summary.Failed == 0
                ? $"处理完成：成功 {summary.Succeeded}"
                : $"处理完成：成功 {summary.Succeeded}，失败 {summary.Failed}";
        StatusMessage = string.IsNullOrWhiteSpace(summary.Notice)
            ? completion
            : $"{completion}；{summary.Notice}";
    }

    private async Task<ProcessingSummary> ProcessOfficeConversionsAsync(
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var batch = Files
            .Where(static file => file.State == JobState.Queued)
            .Select(static file => new PendingOfficeConversion(
                file,
                file.SelectedWorksheetName is null
                    ? null
                    : new OfficeConversionOptions(file.SelectedWorksheetName)))
            .ToArray();
        var reservedOutputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var succeeded = 0;
        var failed = 0;
        var canceled = false;

        for (var index = 0; index < batch.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pendingConversion = batch[index];
            var file = pendingConversion.File;
            EngineStatusTitle = "正在处理";
            EngineStatusDetail = $"{index + 1} / {batch.Length}  {file.FileName}";
            file.MarkValidating();
            var outputPath = CreateUniqueOutputPath(file.FullPath, outputDirectory, reservedOutputPaths);
            file.MarkRunning();
            var progress = new Progress<WorkerProgressMessage>(file.ReportProgress);

            try
            {
                var result = await _officeWorkerClient.ConvertAsync(
                    file.FullPath,
                    outputPath,
                    pendingConversion.Options,
                    progress,
                    cancellationToken);
                if (result.IsSucceeded)
                {
                    file.MarkSucceeded(result.Artifacts.Count > 0 ? result.Artifacts[0] : outputPath);
                    succeeded++;
                }
                else
                {
                    file.MarkFailed(
                        result.ErrorCode ?? "OFFICE_CONVERSION_FAILED",
                        GetActionableError(result.ErrorCode, result.ErrorMessage));
                    failed++;
                }
            }
            catch (OperationCanceledException)
            {
                file.MarkCanceled();
                canceled = true;
                break;
            }
            catch (TimeoutException)
            {
                file.MarkFailed("OFFICE_TIMEOUT", "Office 响应超时，请关闭可能存在的 Office 对话框后重试。");
                failed++;
            }
            catch (Exception)
            {
                file.MarkFailed("OFFICE_WORKER_UNAVAILABLE", "处理引擎异常退出，请重试或导出诊断信息。");
                failed++;
            }
        }

        return new ProcessingSummary(succeeded, failed, canceled, null);
    }

    private async Task<ProcessingSummary> ProcessPdfToExcelAsync(
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var files = Files.Where(static file => file.State == JobState.Queued).ToArray();
        var reservedOutputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var succeeded = 0;
        var failed = 0;
        var canceled = false;
        string? notice = null;

        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            EngineStatusTitle = "正在提取 PDF 表格";
            EngineStatusDetail = $"{index + 1} / {files.Length}  {file.FileName}";
            file.MarkValidating();
            var outputPath = CreateUniqueOutputPath(
                file.FullPath,
                outputDirectory,
                reservedOutputPaths,
                ".xlsx",
                " - 表格");
            file.MarkRunning("正在读取 PDF 表格");
            var progress = new Progress<WorkerProgressMessage>(file.ReportProgress);
            try
            {
                var result = await _pdfTableOperationsClient!.ConvertAsync(
                    new PdfToExcelRequest(
                        file.FullPath,
                        outputPath,
                        SelectedPdfOcrMode.Mode,
                        WorkerPdfWorksheetMode.OneWorksheetPerPage,
                        Password: string.Empty),
                    progress,
                    cancellationToken);
                if (result.IsSucceeded)
                {
                    var resultPath = result.Artifacts.Count > 0 ? result.Artifacts[0] : outputPath;
                    file.MarkSucceeded(resultPath, GetPdfToExcelSuccessStatus(result.Metrics));
                    succeeded++;
                    notice = result.Notices.Count > 0
                        ? GetPdfTableNotice(result.Notices[^1])
                        : notice;
                }
                else
                {
                    file.MarkFailed(
                        result.ErrorCode ?? "PDF_TABLE_EXTRACTION_FAILED",
                        GetActionableError(result.ErrorCode, result.ErrorMessage));
                    failed++;
                }
            }
            catch (OperationCanceledException)
            {
                file.MarkCanceled();
                canceled = true;
                break;
            }
            catch (TimeoutException)
            {
                file.MarkFailed("PDF_TABLE_TIMEOUT", "PDF 表格提取超时，请缩小文件后重试。");
                failed++;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                file.MarkFailed(
                    "PDF_TABLE_WORKER_UNAVAILABLE",
                    "PDF 表格提取引擎异常退出，请重试或导出诊断信息。");
                failed++;
            }
        }

        return new ProcessingSummary(succeeded, failed, canceled, notice);
    }

    private async Task<ProcessingSummary> ProcessExcelOperationsAsync(
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var files = Files.Where(static file => file.State == JobState.Queued).ToArray();
        var options = new ExcelOperationOptions(
            IncludeHiddenWorksheets,
            PreserveExternalLinks,
            SkipUnsafeCompressionSheets,
            ConvertLegacyWorkbookToOpenXml,
            SelectedExcelImageCompressionProfile.Level);
        if (SelectedExcelToolMode.Mode == ExcelToolMode.Merge)
        {
            return await MergeExcelWorkbooksAsync(files, outputDirectory, options, cancellationToken);
        }

        var succeeded = 0;
        var failed = 0;
        var canceled = false;
        string? notice = null;
        var reservedOutputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            EngineStatusTitle = "正在处理 Excel";
            EngineStatusDetail = $"{index + 1} / {files.Length}  {file.FileName}";
            file.MarkValidating();
            var outputPath = SelectedExcelToolMode.Mode == ExcelToolMode.Split
                ? CreateUniqueOutputDirectoryPath(
                    outputDirectory,
                    $"{Path.GetFileNameWithoutExtension(file.FullPath)} - 拆分",
                    reservedOutputPaths)
                : CreateUniqueOutputPath(
                    file.FullPath,
                    outputDirectory,
                    reservedOutputPaths,
                    string.Equals(file.Extension, ".xls", StringComparison.OrdinalIgnoreCase) &&
                        !ConvertLegacyWorkbookToOpenXml
                        ? ".xls"
                        : ".xlsx",
                    " - 压缩");
            var request = SelectedExcelToolMode.Mode == ExcelToolMode.Split
                ? ExcelOperationRequest.Split(file.FullPath, outputPath, options)
                : ExcelOperationRequest.Compress(file.FullPath, outputPath, options);
            file.MarkRunning();
            var progress = new Progress<WorkerProgressMessage>(file.ReportProgress);
            try
            {
                var result = await _excelOperationsClient!.ExecuteAsync(
                    request,
                    progress,
                    cancellationToken);
                if (result.IsSucceeded)
                {
                    var resultPath = SelectedExcelToolMode.Mode == ExcelToolMode.Split
                        ? outputPath
                        : result.Artifacts.Count > 0 ? result.Artifacts[0] : outputPath;
                    file.MarkSucceeded(
                        resultPath,
                        GetExcelSuccessStatus(
                            SelectedExcelToolMode.Mode,
                            result.Metrics,
                            result.Artifacts.Count));
                    succeeded++;
                    notice = result.Notices.Count > 0 ? result.Notices[^1].Message : notice;
                }
                else
                {
                    file.MarkFailed(
                        result.ErrorCode ?? "EXCEL_OPERATION_FAILED",
                        GetActionableError(result.ErrorCode, result.ErrorMessage));
                    failed++;
                }
            }
            catch (OperationCanceledException)
            {
                file.MarkCanceled();
                canceled = true;
                break;
            }
            catch (TimeoutException)
            {
                file.MarkFailed("OFFICE_TIMEOUT", "Excel 响应超时，请关闭可能存在的 Excel 对话框后重试。");
                failed++;
            }
            catch (Exception)
            {
                file.MarkFailed("OFFICE_WORKER_UNAVAILABLE", "Excel 处理引擎异常退出，请重试或导出诊断信息。");
                failed++;
            }
            finally
            {
                if (SelectedExcelToolMode.Mode == ExcelToolMode.Split && !file.IsSucceeded)
                {
                    TryDeleteEmptyDirectory(outputPath);
                }
            }
        }

        return new ProcessingSummary(succeeded, failed, canceled, notice);
    }

    private async Task<ProcessingSummary> MergeExcelWorkbooksAsync(
        QueuedFileViewModel[] files,
        string outputDirectory,
        ExcelOperationOptions options,
        CancellationToken cancellationToken)
    {
        var outputPath = CreateUniqueNamedOutputPath(
            outputDirectory,
            "合并工作簿",
            ".xlsx");
        foreach (var file in files)
        {
            file.MarkValidating();
            file.MarkRunning();
        }

        EngineStatusTitle = "正在合并 Excel";
        EngineStatusDetail = $"按队列顺序合并 {files.Length} 个工作簿";
        var progress = new Progress<WorkerProgressMessage>(message =>
        {
            foreach (var file in files)
            {
                file.ReportProgress(message);
            }
        });

        try
        {
            var result = await _excelOperationsClient!.ExecuteAsync(
                ExcelOperationRequest.Merge(
                    files.Select(static file => file.FullPath).ToArray(),
                    outputPath,
                    options),
                progress,
                cancellationToken);
            if (result.IsSucceeded)
            {
                var resultPath = result.Artifacts.Count > 0 ? result.Artifacts[0] : outputPath;
                foreach (var file in files)
                {
                    file.MarkSucceeded(
                        resultPath,
                        GetExcelSuccessStatus(
                            ExcelToolMode.Merge,
                            result.Metrics,
                            result.Artifacts.Count));
                }

                return new ProcessingSummary(
                    files.Length,
                    0,
                    false,
                    result.Notices.Count > 0 ? result.Notices[^1].Message : null);
            }

            var message = GetActionableError(result.ErrorCode, result.ErrorMessage);
            foreach (var file in files)
            {
                file.MarkFailed(result.ErrorCode ?? "EXCEL_MERGE_FAILED", message);
            }

            return new ProcessingSummary(0, files.Length, false, null);
        }
        catch (OperationCanceledException)
        {
            foreach (var file in files.Where(static file => file.IsProcessing))
            {
                file.MarkCanceled();
            }

            return new ProcessingSummary(0, 0, true, null);
        }
        catch (TimeoutException)
        {
            foreach (var file in files)
            {
                file.MarkFailed("OFFICE_TIMEOUT", "Excel 合并超时，请关闭可能存在的 Excel 对话框后重试。");
            }

            return new ProcessingSummary(0, files.Length, false, null);
        }
        catch (Exception)
        {
            foreach (var file in files)
            {
                file.MarkFailed("OFFICE_WORKER_UNAVAILABLE", "Excel 处理引擎异常退出，请重试或导出诊断信息。");
            }

            return new ProcessingSummary(0, files.Length, false, null);
        }
    }

    private async Task<ProcessingSummary> ProcessPdfOperationsAsync(
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var files = Files.Where(static file => file.State == JobState.Queued).ToArray();
        if (SelectedPdfToolMode.Mode == PdfToolMode.Merge)
        {
            return await MergePdfFilesAsync(files, outputDirectory, cancellationToken);
        }

        var mode = SelectedPdfToolMode.Mode;
        var splitSelection = mode == PdfToolMode.Split ? CreatePdfSplitSelection() : null;
        var compressionStrength = PdfCompressionStrength;
        var reservedOutputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reservedOutputDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var succeeded = 0;
        var failed = 0;
        var canceled = false;
        string? notice = null;

        for (var index = 0; index < files.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[index];
            EngineStatusTitle = mode == PdfToolMode.Split ? "正在拆分 PDF" : "正在压缩 PDF";
            EngineStatusDetail = $"{index + 1} / {files.Length}  {file.FileName}";
            file.MarkValidating();
            file.MarkRunning();

            string resultPath;
            OperationExecutionResult result;
            string? splitDirectory = null;
            try
            {
                if (mode == PdfToolMode.Split)
                {
                    splitDirectory = CreateUniqueOutputDirectoryPath(
                        outputDirectory,
                        $"{Path.GetFileNameWithoutExtension(file.FullPath)} - 拆分",
                        reservedOutputDirectories);
                    var splitOutputPath = Path.Combine(splitDirectory, "拆分结果.pdf");
                    result = await _pdfOperationsClient!.SplitAsync(
                        new PdfSplitRequest(
                            file.FullPath,
                            splitOutputPath,
                            splitSelection!,
                            null),
                        cancellationToken);
                    resultPath = splitDirectory;
                }
                else
                {
                    var outputPath = CreateUniqueOutputPath(
                        file.FullPath,
                        outputDirectory,
                        reservedOutputPaths,
                        ".pdf",
                        " - 压缩");
                    result = await _pdfOperationsClient!.OptimizeAsync(
                        new PdfOptimizeRequest(
                            file.FullPath,
                            outputPath,
                            compressionStrength,
                            null),
                        cancellationToken);
                    resultPath = result.Artifacts.Count > 0 ? result.Artifacts[0] : outputPath;
                }

                if (result.IsSucceeded)
                {
                    if (mode == PdfToolMode.Split && PdfSplitOutputAsZip)
                    {
                        resultPath = CreateSplitArchive(
                            file.FullPath,
                            outputDirectory,
                            result.Artifacts,
                            cancellationToken);
                        TryDeleteGeneratedSplitDirectory(splitDirectory, result.Artifacts);
                    }

                    file.MarkSucceeded(resultPath, GetPdfSuccessStatus(mode, result.Metrics));
                    succeeded++;
                    notice = result.Notices.Count > 0 ? GetPdfNotice(result.Notices[^1]) : notice;
                }
                else
                {
                    file.MarkFailed(
                        result.ErrorCode ?? "PDF_OPERATION_FAILED",
                        GetActionableError(result.ErrorCode, result.ErrorMessage));
                    failed++;
                    TryDeleteEmptyDirectory(splitDirectory);
                }
            }
            catch (OperationCanceledException)
            {
                file.MarkCanceled();
                TryDeleteEmptyDirectory(splitDirectory);
                canceled = true;
                break;
            }
            catch (TimeoutException)
            {
                file.MarkFailed("QPDF_TIMEOUT", "PDF 处理超时，请重试。");
                TryDeleteEmptyDirectory(splitDirectory);
                failed++;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                file.MarkFailed("PDF_ENGINE_UNAVAILABLE", "PDF 处理引擎异常，请重试或导出诊断信息。");
                TryDeleteEmptyDirectory(splitDirectory);
                failed++;
            }
        }

        return new ProcessingSummary(succeeded, failed, canceled, notice);
    }

    private async Task<ProcessingSummary> MergePdfFilesAsync(
        QueuedFileViewModel[] files,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var outputPath = CreateUniqueNamedOutputPath(outputDirectory, "合并 PDF", ".pdf");
        foreach (var file in files)
        {
            file.MarkValidating();
            file.MarkRunning();
        }

        EngineStatusTitle = "正在合并 PDF";
        EngineStatusDetail = $"按队列顺序合并 {files.Length} 个 PDF";
        try
        {
            var result = await _pdfOperationsClient!.MergeAsync(
                new PdfMergeRequest(
                    files.Select(static file => file.FullPath).ToArray(),
                    outputPath,
                    null),
                cancellationToken);
            if (result.IsSucceeded)
            {
                var resultPath = result.Artifacts.Count > 0 ? result.Artifacts[0] : outputPath;
                foreach (var file in files)
                {
                    file.MarkSucceeded(resultPath);
                }

                return new ProcessingSummary(
                    files.Length,
                    0,
                    false,
                    result.Notices.Count > 0 ? GetPdfNotice(result.Notices[^1]) : null);
            }

            var message = GetActionableError(result.ErrorCode, result.ErrorMessage);
            foreach (var file in files)
            {
                file.MarkFailed(result.ErrorCode ?? "PDF_MERGE_FAILED", message);
            }

            return new ProcessingSummary(0, files.Length, false, null);
        }
        catch (OperationCanceledException)
        {
            foreach (var file in files.Where(static file => file.IsProcessing))
            {
                file.MarkCanceled();
            }

            return new ProcessingSummary(0, 0, true, null);
        }
        catch (Exception exception) when (
            exception is TimeoutException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            foreach (var file in files)
            {
                file.MarkFailed("PDF_ENGINE_UNAVAILABLE", "PDF 合并引擎异常，请重试或导出诊断信息。");
            }

            return new ProcessingSummary(0, files.Length, false, null);
        }
    }

    private PdfSplitSelection CreatePdfSplitSelection() => SelectedPdfSplitMode.Mode switch
    {
        PdfSplitMode.CustomRange => PdfSplitSelection.CustomRange(PdfPageRange.Trim()),
        PdfSplitMode.EveryNPages => PdfSplitSelection.EveryNPages(PdfPagesPerFile),
        PdfSplitMode.EveryPage => PdfSplitSelection.EveryPage(),
        PdfSplitMode.OddPages => PdfSplitSelection.OddPages(),
        PdfSplitMode.EvenPages => PdfSplitSelection.EvenPages(),
        PdfSplitMode.VisualCuts => PdfSplitSelection.VisualCuts(
            PdfPageThumbnails
                .Where(static page => page.IsCutBefore)
                .Select(static page => page.PageNumber - 1)
                .ToArray()),
        _ => throw new InvalidOperationException("未知的 PDF 拆分方式。"),
    };

    private bool IsPdfSplitSelectionValid(QueuedFileViewModel[] files)
    {
        if (SelectedPdfSplitMode.Mode == PdfSplitMode.VisualCuts)
        {
            return files.Length == 1 &&
                files[0].PdfPageCount == PdfPageThumbnails.Count &&
                PdfPageThumbnails.Any(static page => page.IsCutBefore);
        }

        if (SelectedPdfSplitMode.Mode == PdfSplitMode.EveryNPages)
        {
            return PdfPagesPerFile > 0;
        }

        if (SelectedPdfSplitMode.Mode != PdfSplitMode.CustomRange ||
            string.IsNullOrWhiteSpace(PdfPageRange))
        {
            return SelectedPdfSplitMode.Mode != PdfSplitMode.CustomRange;
        }

        try
        {
            return files.All(file =>
                file.PdfPageCount is { } pageCount &&
                PdfPageRangeParser.Parse(PdfPageRange, pageCount).Count > 0);
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException)
        {
            return false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanTogglePdfCut))]
    private void TogglePdfCut(PdfPageThumbnailViewModel? page)
    {
        if (page is null)
        {
            return;
        }

        page.IsCutBefore = !page.IsCutBefore;
        OnPropertyChanged(nameof(PdfVisualSplitSummary));
        StartProcessingCommand.NotifyCanExecuteChanged();
    }

    private bool CanTogglePdfCut(PdfPageThumbnailViewModel? page) =>
        page?.CanCutBefore == true && IsPdfVisualSplitMode && !IsProcessing;

    private void RefreshPdfVisualPreview()
    {
        _pdfThumbnailCancellation?.Cancel();
        _pdfThumbnailCancellation = null;
        PdfPageThumbnails.Clear();
        OnPropertyChanged(nameof(ShowPdfVisualPreview));
        OnPropertyChanged(nameof(PdfVisualSplitSummary));
        StartProcessingCommand.NotifyCanExecuteChanged();

        if (_pdfThumbnailRenderer is null ||
            !IsPdfVisualSplitMode ||
            Files.Count != 1 ||
            Files[0].PdfPageCount is not { } pageCount ||
            !Files[0].IsPdfPreflightResolved)
        {
            PdfPreviewStatus = string.Empty;
            IsPdfPreviewLoading = false;
            return;
        }

        var previousDirectory = _pdfThumbnailDirectory;
        if (previousDirectory is not null && _pdfThumbnailTask.IsCompleted)
        {
            TryDeletePreviewDirectory(previousDirectory);
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        _pdfThumbnailCancellation = cancellation;
        var directory = Path.Combine(
            Path.GetTempPath(),
            "DocPivot",
            "pdf-preview",
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        _pdfThumbnailDirectory = directory;
        IsPdfPreviewLoading = true;
        PdfPreviewStatus = $"正在生成 {pageCount} 页预览";
        _pdfThumbnailTask = LoadPdfVisualPreviewAsync(
            Files[0],
            pageCount,
            directory,
            cancellation);
    }

    private async Task LoadPdfVisualPreviewAsync(
        QueuedFileViewModel file,
        int pageCount,
        string directory,
        CancellationTokenSource cancellation)
    {
        var completed = false;
        try
        {
            var thumbnails = await _pdfThumbnailRenderer!.RenderAsync(
                file.FullPath,
                pageCount,
                directory,
                null,
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_pdfThumbnailCancellation, cancellation) ||
                !ReferenceEquals(Files.FirstOrDefault(), file))
            {
                return;
            }

            for (var index = 0; index < thumbnails.Count; index++)
            {
                PdfPageThumbnails.Add(new PdfPageThumbnailViewModel(index + 1, thumbnails[index]));
            }

            completed = true;
            PdfPreviewStatus = "点击页间剪刀设置拆分位置";
            OnPropertyChanged(nameof(ShowPdfVisualPreview));
            OnPropertyChanged(nameof(PdfVisualSplitSummary));
            StartProcessingCommand.NotifyCanExecuteChanged();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Queue changes and shutdown intentionally cancel stale previews.
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            if (ReferenceEquals(_pdfThumbnailCancellation, cancellation))
            {
                PdfPreviewStatus = "无法生成页面预览，请重试 PDF 检查";
            }
        }
        finally
        {
            if (ReferenceEquals(_pdfThumbnailCancellation, cancellation))
            {
                _pdfThumbnailCancellation = null;
                IsPdfPreviewLoading = false;
            }

            cancellation.Dispose();
            if (!completed)
            {
                TryDeletePreviewDirectory(directory);
            }
        }
    }

    private static void TryDeletePreviewDirectory(string directory)
    {
        try
        {
            var previewRoot = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "DocPivot",
                "pdf-preview"));
            var candidate = Path.GetFullPath(directory);
            if (candidate.StartsWith(
                    previewRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) &&
                Directory.Exists(candidate))
            {
                Directory.Delete(candidate, recursive: true);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Preview cleanup is best effort and never changes the job result.
        }
    }

    private static string CreateSplitArchive(
        string inputPath,
        string outputDirectory,
        IReadOnlyList<string> artifacts,
        CancellationToken cancellationToken)
    {
        if (artifacts.Count == 0)
        {
            throw new InvalidOperationException("PDF split did not produce files to archive.");
        }

        var archivePath = CreateUniqueNamedOutputPath(
            outputDirectory,
            $"{Path.GetFileNameWithoutExtension(inputPath)} - 拆分",
            ".zip");
        var stagingPath = AtomicOutputFile.CreateStagingPath(archivePath, Guid.NewGuid());
        try
        {
            using (var archive = ZipFile.Open(stagingPath, ZipArchiveMode.Create))
            {
                foreach (var artifact in artifacts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    archive.CreateEntryFromFile(
                        artifact,
                        Path.GetFileName(artifact),
                        CompressionLevel.SmallestSize);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            AtomicOutputFile.Commit(stagingPath, archivePath);
            return archivePath;
        }
        finally
        {
            if (File.Exists(stagingPath))
            {
                File.Delete(stagingPath);
            }
        }
    }

    private static void TryDeleteGeneratedSplitDirectory(
        string? splitDirectory,
        IReadOnlyList<string> artifacts)
    {
        if (splitDirectory is null || !Directory.Exists(splitDirectory))
        {
            return;
        }

        var normalizedDirectory = Path.GetFullPath(splitDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var artifact in artifacts)
        {
            var normalizedArtifact = Path.GetFullPath(artifact);
            if (normalizedArtifact.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(normalizedArtifact);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    return;
                }
            }
        }

        try
        {
            Directory.Delete(splitDirectory, recursive: false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // A generated directory containing unexpected files is deliberately retained.
        }
    }

    private static string GetPdfToExcelSuccessStatus(
        IReadOnlyDictionary<string, string> metrics)
    {
        metrics.TryGetValue("tableCount", out var tableCount);
        metrics.TryGetValue("worksheetCount", out var worksheetCount);
        metrics.TryGetValue("ocrPageCount", out var ocrPageCount);
        metrics.TryGetValue("lowConfidenceCellCount", out var lowConfidenceCellCount);
        var status = $"已提取 {tableCount ?? "0"} 个表格 · {worksheetCount ?? "0"} 个工作表";
        if (int.TryParse(ocrPageCount, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ocrPages) &&
            ocrPages > 0)
        {
            status += $" · OCR {ocrPages} 页";
        }

        if (int.TryParse(
            lowConfidenceCellCount,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var lowConfidenceCells) &&
            lowConfidenceCells > 0)
        {
            status += $" · 待复核 {lowConfidenceCells} 格";
        }

        return status;
    }

    private static string GetPdfSuccessStatus(
        PdfToolMode mode,
        IReadOnlyDictionary<string, string> metrics)
    {
        if (mode != PdfToolMode.Compress)
        {
            return "处理完成";
        }

        var details = new List<string> { "压缩完成" };
        if (TryGetMetricInt64(metrics, "originalBytes", out var originalBytes) &&
            TryGetMetricInt64(metrics, "outputBytes", out var outputBytes))
        {
            details.Add($"{FormatByteCount(originalBytes)} → {FormatByteCount(outputBytes)}");
        }

        if (metrics.TryGetValue("optimizationApplied", out var applied) &&
            string.Equals(applied, "false", StringComparison.OrdinalIgnoreCase))
        {
            details.Add("无可用压缩收益");
            details.Add("源内容保留");
            return string.Join(" · ", details);
        }

        if (metrics.TryGetValue("savedPercent", out var savedPercent))
        {
            details.Add($"节省 {savedPercent}%");
        }

        if (metrics.TryGetValue("compressionStrength", out var strength) && strength == "0")
        {
            details.Add("无损");
        }
        else
        {
            if (metrics.TryGetValue("imageDpi", out var imageDpi))
            {
                details.Add($"{imageDpi} DPI");
            }

            if (metrics.TryGetValue("jpegQuality", out var jpegQuality))
            {
                details.Add($"JPEG {jpegQuality}%");
            }

            if (metrics.TryGetValue("impactLevel", out var impactLevel))
            {
                details.Add(impactLevel switch
                {
                    "low" => "低画质影响",
                    "medium" => "中等画质影响",
                    "high" => "高画质影响",
                    _ => "画质影响未知",
                });
            }
        }

        if (metrics.TryGetValue("searchableTextStatus", out var searchableTextStatus))
        {
            details.Add(searchableTextStatus switch
            {
                "verified" => "文字层已验证",
                "not-present" => "未检测到文字层",
                "source-retained" => "源文字层保留",
                _ => "文字层状态未知",
            });
        }

        return string.Join(" · ", details);
    }

    private static string GetExcelSuccessStatus(
        ExcelToolMode mode,
        IReadOnlyDictionary<string, string> metrics,
        int artifactCount)
    {
        if (mode == ExcelToolMode.Merge)
        {
            return TryGetMetricInt64(metrics, "worksheetCount", out var worksheetCount)
                ? $"已合并 {worksheetCount} 张工作表"
                : "合并完成";
        }

        if (mode == ExcelToolMode.Split)
        {
            return $"已拆分 {artifactCount} 张工作表";
        }

        var retainedSource = metrics.TryGetValue("sourceContentRetained", out var retainedText) &&
            string.Equals(retainedText, "true", StringComparison.OrdinalIgnoreCase);
        var details = new List<string>
        {
            retainedSource ? "无可用压缩收益" : "压缩完成",
        };
        if (TryGetMetricInt64(metrics, "bytesBefore", out var bytesBefore) &&
            TryGetMetricInt64(metrics, "bytesAfter", out var bytesAfter))
        {
            details.Add($"{FormatByteCount(bytesBefore)} → {FormatByteCount(bytesAfter)}");
            if (bytesBefore > 0)
            {
                var changePercent = Math.Abs((bytesBefore - bytesAfter) * 100d / bytesBefore);
                details.Add(bytesAfter <= bytesBefore
                    ? $"节省 {changePercent.ToString("0.0", CultureInfo.InvariantCulture)}%"
                    : $"增加 {changePercent.ToString("0.0", CultureInfo.InvariantCulture)}%");
            }
        }

        if (retainedSource)
        {
            details.Add("已保留源内容");
        }
        else
        {
            if (TryGetMetricInt64(metrics, "cleanedWorksheetCount", out var cleanedCount) &&
                cleanedCount > 0)
            {
                details.Add($"清理 {cleanedCount} 张表");
            }

            if (TryGetMetricInt64(metrics, "compressedImageCount", out var imageCount) &&
                imageCount > 0)
            {
                details.Add($"重采样 {imageCount} 张图");
            }
        }

        if (TryGetMetricInt64(metrics, "skippedWorksheetCount", out var skippedCount) &&
            skippedCount > 0)
        {
            details.Add($"跳过 {skippedCount} 张风险表");
        }

        if (metrics.TryGetValue("convertedLegacyWorkbook", out var convertedText) &&
            string.Equals(convertedText, "true", StringComparison.OrdinalIgnoreCase))
        {
            details.Add("XLS → XLSX");
        }

        return string.Join(" · ", details);
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

    private static string GetPdfNotice(WorkerNotice notice) => notice.Code switch
    {
        "PDF_MERGE_PRIMARY_DOCUMENT_INFO" =>
            "文档级信息以队列中的首个 PDF 为准，后续文件仅合并页面。",
        "PDF_NO_COMPRESSION_BENEFIT" => "未发现可用压缩收益，已保留源内容。",
        "PDF_DECRYPTED_WITHOUT_COMPRESSION" => "PDF 已成功解锁；无损重写未获得体积收益。",
        "PDF_LOSSY_PRESERVATION_LIMITS" =>
            "页数与可搜索文字已验证；批注、表单和附件不保证保留。",
        _ => notice.Message,
    };

    private static string GetPdfTableNotice(WorkerNotice notice) => notice.Code switch
    {
        "OCR_CONTENT_REQUIRES_REVIEW" => "工作簿包含本地 OCR 结果，请复核提取报告与高亮单元格。",
        "LOW_CONFIDENCE_CELLS" => "低置信度单元格已在工作簿中高亮。",
        "TABLE_STRUCTURE_UNCERTAIN" => "部分页面未检测到稳定列结构，已按低置信度行文本导出。",
        _ => notice.Message,
    };

    private bool CanStartProcessing()
    {
        if (IsProcessing ||
            !HasPendingFiles ||
            (!IsBatchRenameTool && string.IsNullOrWhiteSpace(OutputDirectory)))
        {
            return false;
        }

        var pendingFiles = Files.Where(static file => file.State == JobState.Queued).ToArray();
        if (IsOfficeTool)
        {
            return IsEngineReady && pendingFiles.All(static file => file.IsWorksheetSelectionResolved);
        }

        if (IsPdfToExcelTool)
        {
            return IsSelectedToolReady &&
                pendingFiles.All(static file => file.IsPdfPreflightResolved);
        }

        if (IsExcelOperationsTool)
        {
            return IsSelectedToolReady &&
                pendingFiles.All(static file => file.IsExcelToolPreflightResolved) &&
                (SelectedExcelToolMode.Mode != ExcelToolMode.Merge || pendingFiles.Length >= 2);
        }

        if (IsPdfOperationsTool)
        {
            if (!IsSelectedToolReady ||
                pendingFiles.Any(static file => !file.IsPdfPreflightResolved))
            {
                return false;
            }

            return SelectedPdfToolMode.Mode switch
            {
                PdfToolMode.Merge => pendingFiles.Length >= 2,
                PdfToolMode.Split => IsPdfSplitSelectionValid(pendingFiles),
                PdfToolMode.Compress => PdfCompressionStrength is >= 0 and <= 100,
                _ => false,
            };
        }

        if (IsBatchRenameTool)
        {
            return IsSelectedToolReady && CanStartBatchRename();
        }

        return false;
    }

    private bool IsPdfEngineReadyForSelection()
    {
        if (_pdfOperationsClient is null)
        {
            return false;
        }

        if (_pdfOperationsClient is not IPdfEngineStatusProvider)
        {
            return true;
        }

        return SelectedPdfToolMode.Mode == PdfToolMode.Compress && PdfCompressionStrength > 0
            ? _pdfEngineStatus?.IsLossyCompressionReady == true
            : _pdfEngineStatus?.IsCoreReady == true;
    }

    private bool IsPdfTableEngineReadyForSelection()
    {
        if (_pdfTableOperationsClient is null || _pdfOperationsClient is null)
        {
            return false;
        }

        var pdfCoreReady = _pdfOperationsClient is not IPdfEngineStatusProvider ||
            _pdfEngineStatus?.IsCoreReady == true;
        if (!pdfCoreReady || _pdfTableEngineStatus is null)
        {
            return false;
        }

        return SelectedPdfOcrMode.Mode == WorkerPdfOcrMode.DigitalTextOnly
            ? _pdfTableEngineStatus.IsDigitalTextReady && _pdfTableEngineStatus.IsXlsxReady
            : _pdfTableEngineStatus.IsReady;
    }

    [RelayCommand(CanExecute = nameof(CanCancelProcessing))]
    public void CancelProcessing()
    {
        _processingCancellation?.Cancel();
        if (IsProcessing)
        {
            StatusMessage = "正在取消当前任务";
        }
    }

    private bool CanCancelProcessing() => IsProcessing;

    public Task WaitForProcessingCompletionAsync() =>
        StartProcessingCommand.ExecutionTask ?? Task.CompletedTask;

    public async Task WaitForWorksheetDiscoveryAsync()
    {
        Task[] tasks;
        lock (_worksheetDiscoverySync)
        {
            tasks = _worksheetDiscoveryTasks.Values.ToArray();
        }

        if (tasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // Removal and shutdown intentionally cancel outstanding discovery work.
        }
    }

    public async Task WaitForPdfPreflightAsync()
    {
        Task[] tasks;
        lock (_pdfPreflightSync)
        {
            tasks = _pdfPreflightTasks.Values.ToArray();
        }

        if (tasks.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // Removal and shutdown intentionally cancel outstanding preflight work.
        }
    }

    public async Task WaitForPdfPreviewAsync()
    {
        try
        {
            await _pdfThumbnailTask;
        }
        catch (OperationCanceledException)
        {
            // Queue changes and shutdown intentionally cancel preview rendering.
        }
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
        OnPropertyChanged(nameof(IsPdfMergeMode));
        OnPropertyChanged(nameof(IsPdfSplitMode));
        OnPropertyChanged(nameof(IsPdfCompressMode));
        OnPropertyChanged(nameof(IsPdfCustomRangeMode));
        OnPropertyChanged(nameof(IsPdfEveryNPagesMode));
        OnPropertyChanged(nameof(IsPdfVisualSplitMode));
        OnPropertyChanged(nameof(ShowPdfVisualPreview));
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

    partial void OnPdfWorksheetPerPageChanged(bool value)
    {
        StartProcessingCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedPdfSplitModeChanged(PdfSplitModeOption value)
    {
        OnPropertyChanged(nameof(IsPdfCustomRangeMode));
        OnPropertyChanged(nameof(IsPdfEveryNPagesMode));
        OnPropertyChanged(nameof(IsPdfVisualSplitMode));
        OnPropertyChanged(nameof(ShowPdfVisualPreview));
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
        foreach (var queue in _queues.Values)
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
    }

    private void OnQueueChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (QueuedFileViewModel file in e.OldItems)
            {
                CancelWorksheetDiscovery(file);
                CancelPdfPreflight(file);
                file.PropertyChanged -= OnFilePropertyChanged;
            }
        }

        if (e.NewItems is not null)
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
            RefreshPdfVisualPreview();
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

    private (string Summary, string Detail) GetExcelMergePreview()
    {
        var pendingFiles = Files.Where(static file => file.State == JobState.Queued).ToArray();
        if (pendingFiles.Length < 2)
        {
            return ("至少需要 2 个工作簿", "添加工作簿后显示合并名称分配");
        }

        if (pendingFiles.Any(static file => !file.IsExcelToolPreflightResolved))
        {
            return ("等待兼容性预检", "工作表名称读取完成后生成预览");
        }

        var sourceNames = pendingFiles
            .SelectMany(static file => file.ExcelToolWorksheetNames)
            .ToArray();
        if (sourceNames.Length == 0)
        {
            return ("未发现可合并工作表", "请重新检查工作簿兼容性");
        }

        var allocatedNames = ExcelWorksheetNameAllocator.Allocate(sourceNames);
        var changes = sourceNames
            .Zip(allocatedNames)
            .Where(static pair => !string.Equals(pair.First, pair.Second, StringComparison.Ordinal))
            .Select(static pair => $"{pair.First} → {pair.Second}")
            .ToArray();
        var summary = changes.Length == 0
            ? $"{sourceNames.Length} 张表 · 名称保持不变"
            : $"{sourceNames.Length} 张表 · {changes.Length} 个名称调整";
        var detail = changes.Length == 0
            ? "未检测到同名或无效工作表名称"
            : string.Join("；", changes.Take(8)) +
                (changes.Length > 8 ? $"；另有 {changes.Length - 8} 项" : string.Empty);
        return (summary, detail);
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

    private void UpdateEngineStatus()
    {
        if (IsPdfToExcelTool)
        {
            if (_pdfOperationsClient is null || _pdfTableOperationsClient is null)
            {
                EngineStatusTitle = "PDF 转 Excel 不可用";
                EngineStatusDetail = "未找到本地 PDF 表格提取引擎";
                return;
            }

            if (_pdfOperationsClient is IPdfEngineStatusProvider && _pdfEngineStatus is null ||
                _pdfTableEngineStatus is null)
            {
                EngineStatusTitle = "正在检测表格引擎";
                EngineStatusDetail = "正在校验 PdfPig、Ghostscript、Tesseract 与 Open XML";
                return;
            }

            if (_pdfEngineStatus?.IsCoreReady == false)
            {
                EngineStatusTitle = "PDF 转 Excel 不可用";
                EngineStatusDetail = _pdfEngineStatus.Qpdf.ErrorMessage ?? "PDF 预检引擎校验失败";
                return;
            }

            if (!_pdfTableEngineStatus.IsDigitalTextReady || !_pdfTableEngineStatus.IsXlsxReady)
            {
                EngineStatusTitle = "PDF 转 Excel 不可用";
                EngineStatusDetail = _pdfTableEngineStatus.ErrorMessage ?? "表格提取 Worker 校验失败";
                return;
            }

            if (SelectedPdfOcrMode.Mode != WorkerPdfOcrMode.DigitalTextOnly &&
                !_pdfTableEngineStatus.IsLocalOcrReady)
            {
                EngineStatusTitle = "本地 OCR 不可用";
                EngineStatusDetail = _pdfTableEngineStatus.ErrorMessage ?? "请选择“仅文字”或重新安装应用";
                return;
            }

            EngineStatusTitle = "PDF 转 Excel 已就绪";
            EngineStatusDetail = SelectedPdfOcrMode.Mode switch
            {
                WorkerPdfOcrMode.DigitalTextOnly => "PdfPig · 仅提取可搜索文字层",
                WorkerPdfOcrMode.ForceOcr => "Ghostscript + Tesseract · 全页离线 OCR",
                _ => "PdfPig 优先 · 扫描页自动转本地 OCR",
            };
            return;
        }

        if (IsExcelOperationsTool)
        {
            if (_excelOperationsClient is not null && _officeEngineStatus?.IsExcelReady == true)
            {
                EngineStatusTitle = "Excel 工具已就绪";
                EngineStatusDetail = SelectedExcelToolMode.Mode switch
                {
                    ExcelToolMode.Merge => "将按当前队列顺序合并工作表",
                    ExcelToolMode.Split => "每张工作表输出为独立 XLSX 文件",
                    _ => "安全清理数据边界外的冗余行列",
                };
                return;
            }

            EngineStatusTitle = "Excel 工具不可用";
            EngineStatusDetail = _officeEngineStatus?.ErrorMessage ?? "请检查 Microsoft Excel 安装状态";
            return;
        }

        if (IsPdfOperationsTool)
        {
            if (_pdfOperationsClient is null)
            {
                EngineStatusTitle = "PDF 工具不可用";
                EngineStatusDetail = "未找到本地 PDF 处理引擎";
                return;
            }

            if (_pdfOperationsClient is IPdfEngineStatusProvider && _pdfEngineStatus is null)
            {
                EngineStatusTitle = _pdfEngineProbeError is null ? "正在检测 PDF 引擎" : "PDF 工具不可用";
                EngineStatusDetail = _pdfEngineProbeError ?? "正在校验 qpdf 与 Ghostscript 运行时";
                return;
            }

            if (_pdfEngineStatus?.IsCoreReady == false)
            {
                EngineStatusTitle = "PDF 工具不可用";
                EngineStatusDetail = _pdfEngineStatus.Qpdf.ErrorMessage ?? "qpdf 运行时校验失败";
                return;
            }

            if (SelectedPdfToolMode.Mode == PdfToolMode.Compress &&
                PdfCompressionStrength > 0 &&
                _pdfEngineStatus?.IsLossyCompressionReady == false)
            {
                EngineStatusTitle = "有损压缩不可用";
                EngineStatusDetail = _pdfEngineStatus.Ghostscript.ErrorMessage ?? "Ghostscript 运行时校验失败";
                return;
            }

            EngineStatusTitle = "PDF 工具已就绪";
            EngineStatusDetail = SelectedPdfToolMode.Mode switch
            {
                PdfToolMode.Merge => "qpdf · 按当前队列顺序合并",
                PdfToolMode.Split => "qpdf · 按页码规则拆分",
                _ when PdfCompressionStrength == 0 => "qpdf · 无损结构优化",
                _ => $"Ghostscript · {PdfCompressionDetail}",
            };
            return;
        }

        if (IsBatchRenameTool)
        {
            EngineStatusTitle = _batchRenameExecutor is null
                ? "批量重命名不可用"
                : "批量重命名已就绪";
            EngineStatusDetail = _batchRenameExecutor is null
                ? "未找到本地重命名事务引擎"
                : $"两阶段安全事务 · {RenamePreviewSummary}";
            return;
        }

        if (!IsOfficeTool)
        {
            EngineStatusTitle = "该工具尚未接入";
            EngineStatusDetail = "将按项目里程碑逐项启用";
            return;
        }

        if (_officeEngineStatus?.IsReady == true)
        {
            EngineStatusTitle = "Office 引擎已就绪";
            var version = _officeEngineStatus.Version ?? "版本未知";
            var platform = _officeEngineStatus.Platform ?? "位数未知";
            EngineStatusDetail = $"本机 Microsoft Office {version} · {platform}";
            return;
        }

        if (_officeEngineStatus is null)
        {
            EngineStatusTitle = "正在检测 Office";
            EngineStatusDetail = "正在确认本机 Word 和 Excel 可用性";
            return;
        }

        EngineStatusTitle = "Office 引擎不可用";
        EngineStatusDetail = _officeEngineStatus.ErrorMessage ?? "请检查 Office 安装状态";
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

    private static string GetWorksheetDiscoveryError(string? errorCode) => errorCode switch
    {
        "INPUT_NOT_FOUND" => "源文件不存在或已被移动",
        "INPUT_TOO_LARGE" => "文件超过 100 MB 上限",
        "SIGNATURE_MISMATCH" or "OFFICE_BINARY_TYPE_MISMATCH" or "OFFICE_OPEN_XML_TYPE_MISMATCH" =>
            "文件类型不匹配或文件已损坏",
        "OFFICE_TIMEOUT" => "读取工作表超时，请关闭 Excel 对话框后重试",
        "OFFICE_COM_FAILURE" => "Excel 无法读取该工作簿，请确认文件能正常打开",
        _ => "无法读取工作表，请重试或确认导出全部",
    };

    private static string GetPdfActionableError(string? errorCode, string? engineMessage) => errorCode switch
    {
        "PDF_INPUT_NOT_FOUND" => "PDF 不存在或已被移动",
        "PDF_INPUT_TOO_LARGE" => "PDF 超过 100 MB 上限",
        "PDF_PASSWORD_REQUIRED" => "PDF 已加密，请输入密码后重新检查",
        "PDF_PASSWORD_INVALID" => "PDF 密码不正确，请重新输入后检查",
        "PDF_PAGE_LIMIT_EXCEEDED" => "PDF 超过 1000 页上限",
        "PDF_STRUCTURE_INVALID" or "PDF_METADATA_INVALID" => "PDF 结构损坏或无法读取",
        "QPDF_RUNTIME_MISSING" or "QPDF_RUNTIME_HASH_MISMATCH" or "QPDF_VERSION_MISMATCH" =>
            "本地 PDF 引擎校验失败，请重新安装文枢",
        "QPDF_TIMEOUT" => "PDF 检查超时，请重试",
        _ => string.IsNullOrWhiteSpace(engineMessage) ? "无法检查 PDF，请重试" : engineMessage,
    };

    private sealed record PendingOfficeConversion(
        QueuedFileViewModel File,
        OfficeConversionOptions? Options);

    private sealed record ProcessingSummary(
        int Succeeded,
        int Failed,
        bool Canceled,
        string? Notice);
}
