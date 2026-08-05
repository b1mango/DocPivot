using CommunityToolkit.Mvvm.ComponentModel;
using DocPivot.Core.Contracts;
using DocPivot.Core.Jobs;
using DocPivot.Core.Renaming;
using System.IO;

namespace DocPivot.App.ViewModels;

public sealed class QueuedFileViewModel : ObservableObject
{
    private JobState _state = JobState.Queued;
    private string _statusText = "待处理";
    private string? _errorCode;
    private string? _outputPath;
    private int _progressCurrent;
    private int _progressTotal;
    private WorksheetDiscoveryState _worksheetDiscoveryState;
    private IReadOnlyList<ExcelExportScopeOption> _worksheetOptions = [ExcelExportScopeOption.AllWorksheets];
    private ExcelExportScopeOption _selectedWorksheetOption = ExcelExportScopeOption.AllWorksheets;
    private string? _worksheetSelectionStatusText;
    private bool _hasWorksheetDiscoveryWarning;
    private bool _isWorksheetSelectionLocked;
    private ExcelToolPreflightState _excelToolPreflightState;
    private ExcelWorkbookCompatibilityReport? _excelCompatibility;
    private IReadOnlyList<string> _excelToolWorksheetNames = [];
    private string? _excelToolPreflightStatusText;
    private string? _excelToolPreflightSummaryText;
    private PdfPreflightState _pdfPreflightState;
    private int? _pdfPageCount;
    private bool _hasPdfSignature;
    private bool _isPdfEncrypted;
    private string? _pdfPreflightStatusText;
    private string _renameCalculatedName;
    private string _renamePreviewName;
    private string? _renameManualOverride;
    private RenamePlanItemStatus _renamePlanStatus = RenamePlanItemStatus.Unchanged;
    private string? _renameValidationCode;

    public QueuedFileViewModel(
        string fullPath,
        string fileName,
        string extension,
        long sizeBytes,
        bool enableWorksheetSelection,
        bool enablePdfPreflight = false,
        bool enableRenamePreview = false,
        DateTime? creationTimeUtc = null,
        DateTime? lastWriteTimeUtc = null,
        int renameAddedOrder = 0,
        bool enableExcelToolPreflight = false,
        bool blockSignedPdfChanges = true)
    {
        FullPath = fullPath;
        FileName = fileName;
        Extension = extension;
        SizeBytes = sizeBytes;
        CreationTimeUtc = creationTimeUtc ?? DateTime.UnixEpoch;
        LastWriteTimeUtc = lastWriteTimeUtc ?? DateTime.UnixEpoch;
        RenameAddedOrder = renameAddedOrder;
        RequiresRenamePreview = enableRenamePreview;
        _renameCalculatedName = fileName;
        _renamePreviewName = fileName;
        RequiresWorksheetSelection = enableWorksheetSelection && IsExcelFile;
        _worksheetDiscoveryState = RequiresWorksheetSelection
            ? WorksheetDiscoveryState.Loading
            : WorksheetDiscoveryState.NotApplicable;
        RequiresExcelToolPreflight = enableExcelToolPreflight && IsExcelFile;
        _excelToolPreflightState = RequiresExcelToolPreflight
            ? ExcelToolPreflightState.Loading
            : ExcelToolPreflightState.NotApplicable;
        RequiresPdfPreflight = enablePdfPreflight &&
            string.Equals(Extension, ".pdf", StringComparison.OrdinalIgnoreCase);
        BlocksSignedPdfChanges = blockSignedPdfChanges;
        _pdfPreflightState = RequiresPdfPreflight
            ? PdfPreflightState.Loading
            : PdfPreflightState.NotApplicable;
    }

    public string FullPath { get; }

    public string FileName { get; }

    public string Extension { get; }

    public long SizeBytes { get; }

    public DateTime CreationTimeUtc { get; }

    public DateTime LastWriteTimeUtc { get; }

    public int RenameAddedOrder { get; }

    public bool IsExcelFile =>
        string.Equals(Extension, ".xls", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Extension, ".xlsx", StringComparison.OrdinalIgnoreCase);

    public bool RequiresWorksheetSelection { get; }

    public bool RequiresExcelToolPreflight { get; }

    public bool RequiresPdfPreflight { get; }

    public bool BlocksSignedPdfChanges { get; }

    public bool RequiresRenamePreview { get; }

    public ExcelToolPreflightState ExcelToolPreflightState
    {
        get => _excelToolPreflightState;
        private set
        {
            if (!SetProperty(ref _excelToolPreflightState, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsExcelToolPreflightLoading));
            OnPropertyChanged(nameof(IsExcelToolPreflightResolved));
            OnPropertyChanged(nameof(HasExcelToolPreflightFailure));
            OnPropertyChanged(nameof(CanRetryExcelToolPreflight));
            OnPropertyChanged(nameof(HasExcelToolPreflightWarning));
        }
    }

    public ExcelWorkbookCompatibilityReport? ExcelCompatibility
    {
        get => _excelCompatibility;
        private set
        {
            if (SetProperty(ref _excelCompatibility, value))
            {
                OnPropertyChanged(nameof(HasExcelToolPreflightWarning));
            }
        }
    }

    public IReadOnlyList<string> ExcelToolWorksheetNames
    {
        get => _excelToolWorksheetNames;
        private set => SetProperty(ref _excelToolWorksheetNames, value);
    }

    public string? ExcelToolPreflightStatusText
    {
        get => _excelToolPreflightStatusText;
        private set => SetProperty(ref _excelToolPreflightStatusText, value);
    }

    public string? ExcelToolPreflightSummaryText
    {
        get => _excelToolPreflightSummaryText;
        private set => SetProperty(ref _excelToolPreflightSummaryText, value);
    }

    public bool IsExcelToolPreflightLoading =>
        ExcelToolPreflightState == ExcelToolPreflightState.Loading;

    public bool IsExcelToolPreflightResolved =>
        !RequiresExcelToolPreflight || ExcelToolPreflightState == ExcelToolPreflightState.Ready;

    public bool HasExcelToolPreflightFailure =>
        ExcelToolPreflightState == ExcelToolPreflightState.Failed;

    public bool CanRetryExcelToolPreflight =>
        RequiresExcelToolPreflight && HasExcelToolPreflightFailure && !IsProcessing;

    public bool HasExcelToolPreflightWarning =>
        HasExcelToolPreflightFailure || ExcelCompatibility is
        {
            HasVbaProject: true
        } || ExcelCompatibility is
        {
            ChartSheetCount: > 0
        } || ExcelCompatibility is
        {
            ExternalLinkCount: > 0
        } || ExcelCompatibility?.CompressionRisks.Count > 0;

    public string RenameCalculatedName
    {
        get => _renameCalculatedName;
        private set => SetProperty(ref _renameCalculatedName, value);
    }

    public string RenamePreviewName
    {
        get => _renamePreviewName;
        set
        {
            if (!CanEditRenamePreview || !SetProperty(ref _renamePreviewName, value))
            {
                return;
            }

            RenameManualOverride = value;
        }
    }

    public string? RenameManualOverride
    {
        get => _renameManualOverride;
        private set
        {
            if (SetProperty(ref _renameManualOverride, value))
            {
                OnPropertyChanged(nameof(IsRenameManuallyEdited));
            }
        }
    }

    public RenamePlanItemStatus RenamePlanStatus
    {
        get => _renamePlanStatus;
        private set
        {
            if (SetProperty(ref _renamePlanStatus, value))
            {
                OnPropertyChanged(nameof(RenameWillChange));
                OnPropertyChanged(nameof(RenameHasIssue));
                OnPropertyChanged(nameof(RenameIsUnchanged));
            }
        }
    }

    public string? RenameValidationCode
    {
        get => _renameValidationCode;
        private set => SetProperty(ref _renameValidationCode, value);
    }

    public bool IsRenameManuallyEdited => RenameManualOverride is not null;

    public bool RenameWillChange => RenamePlanStatus == RenamePlanItemStatus.Ready;

    public bool RenameHasIssue => RenamePlanStatus is RenamePlanItemStatus.Invalid or RenamePlanItemStatus.Conflict;

    public bool RenameIsUnchanged => RenamePlanStatus == RenamePlanItemStatus.Unchanged;

    public bool CanEditRenamePreview => RequiresRenamePreview && State == JobState.Queued;

    public string RenameDirectoryText => Path.GetDirectoryName(FullPath) ?? string.Empty;

    public PdfPreflightState PdfPreflightState
    {
        get => _pdfPreflightState;
        private set
        {
            if (!SetProperty(ref _pdfPreflightState, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsPdfPreflightLoading));
            OnPropertyChanged(nameof(IsPdfPreflightResolved));
            OnPropertyChanged(nameof(HasPdfPreflightFailure));
            OnPropertyChanged(nameof(CanRetryPdfPreflight));
            OnPropertyChanged(nameof(HasPdfPreflightWarning));
        }
    }

    public int? PdfPageCount
    {
        get => _pdfPageCount;
        private set
        {
            if (SetProperty(ref _pdfPageCount, value))
            {
                OnPropertyChanged(nameof(PdfPageCountText));
            }
        }
    }

    public bool HasPdfSignature
    {
        get => _hasPdfSignature;
        private set
        {
            if (SetProperty(ref _hasPdfSignature, value))
            {
                OnPropertyChanged(nameof(HasPdfPreflightWarning));
            }
        }
    }

    public bool IsPdfEncrypted
    {
        get => _isPdfEncrypted;
        private set => SetProperty(ref _isPdfEncrypted, value);
    }

    public string? PdfPreflightStatusText
    {
        get => _pdfPreflightStatusText;
        private set => SetProperty(ref _pdfPreflightStatusText, value);
    }

    public bool IsPdfPreflightLoading => PdfPreflightState == PdfPreflightState.Loading;

    public bool IsPdfPreflightResolved =>
        !RequiresPdfPreflight || PdfPreflightState == PdfPreflightState.Ready;

    public bool HasPdfPreflightFailure => PdfPreflightState == PdfPreflightState.Failed;

    public bool CanRetryPdfPreflight =>
        RequiresPdfPreflight && PdfPreflightState == PdfPreflightState.Failed && !IsProcessing;

    public bool HasPdfPreflightWarning =>
        HasPdfPreflightFailure || HasPdfSignature && BlocksSignedPdfChanges;

    public string PdfPageCountText => PdfPageCount is { } pageCount ? $"{pageCount} 页" : string.Empty;

    public WorksheetDiscoveryState WorksheetDiscoveryState
    {
        get => _worksheetDiscoveryState;
        private set
        {
            if (!SetProperty(ref _worksheetDiscoveryState, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsWorksheetSelectionLoading));
            OnPropertyChanged(nameof(IsWorksheetSelectionResolved));
            OnPropertyChanged(nameof(HasWorksheetDiscoveryFailure));
            OnPropertyChanged(nameof(IsWorksheetSelectionEnabled));
            OnPropertyChanged(nameof(CanRetryWorksheetDiscovery));
            OnPropertyChanged(nameof(CanConfirmAllWorksheetsFallback));
        }
    }

    public IReadOnlyList<ExcelExportScopeOption> WorksheetOptions
    {
        get => _worksheetOptions;
        private set => SetProperty(ref _worksheetOptions, value);
    }

    public ExcelExportScopeOption SelectedWorksheetOption
    {
        get => _selectedWorksheetOption;
        set
        {
            if (value is null || !value.IsEnabled || !SetProperty(ref _selectedWorksheetOption, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedWorksheetName));
        }
    }

    public string? WorksheetSelectionStatusText
    {
        get => _worksheetSelectionStatusText;
        private set => SetProperty(ref _worksheetSelectionStatusText, value);
    }

    public bool HasWorksheetDiscoveryWarning
    {
        get => _hasWorksheetDiscoveryWarning;
        private set => SetProperty(ref _hasWorksheetDiscoveryWarning, value);
    }

    public bool IsWorksheetSelectionLoading => WorksheetDiscoveryState == WorksheetDiscoveryState.Loading;

    public bool IsWorksheetSelectionResolved =>
        !RequiresWorksheetSelection || WorksheetDiscoveryState == WorksheetDiscoveryState.Ready;

    public bool HasWorksheetDiscoveryFailure => WorksheetDiscoveryState == WorksheetDiscoveryState.Failed;

    public bool IsWorksheetSelectionEnabled =>
        RequiresWorksheetSelection &&
        WorksheetDiscoveryState == WorksheetDiscoveryState.Ready &&
        !_isWorksheetSelectionLocked &&
        !IsProcessing;

    public bool CanRetryWorksheetDiscovery =>
        RequiresWorksheetSelection &&
        WorksheetDiscoveryState == WorksheetDiscoveryState.Failed &&
        !_isWorksheetSelectionLocked;

    public bool CanConfirmAllWorksheetsFallback => CanRetryWorksheetDiscovery;

    public string? SelectedWorksheetName =>
        SelectedWorksheetOption.Kind == ExcelExportScopeKind.SingleWorksheet
            ? SelectedWorksheetOption.WorksheetName
            : null;

    public JobState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsProcessing));
            OnPropertyChanged(nameof(IsSucceeded));
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(CanOpenResult));
            OnPropertyChanged(nameof(IsWorksheetSelectionEnabled));
            OnPropertyChanged(nameof(CanEditRenamePreview));
            OnPropertyChanged(nameof(CanRetryExcelToolPreflight));
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string? ErrorCode
    {
        get => _errorCode;
        private set => SetProperty(ref _errorCode, value);
    }

    public string? OutputPath
    {
        get => _outputPath;
        private set
        {
            if (SetProperty(ref _outputPath, value))
            {
                OnPropertyChanged(nameof(CanOpenResult));
            }
        }
    }

    public int ProgressCurrent
    {
        get => _progressCurrent;
        private set
        {
            if (SetProperty(ref _progressCurrent, value))
            {
                OnPropertyChanged(nameof(ProgressPercent));
            }
        }
    }

    public int ProgressTotal
    {
        get => _progressTotal;
        private set
        {
            if (SetProperty(ref _progressTotal, value))
            {
                OnPropertyChanged(nameof(ProgressPercent));
            }
        }
    }

    public string SizeText => SizeBytes switch
    {
        >= 1024L * 1024L => $"{SizeBytes / 1024d / 1024d:0.0} MB",
        >= 1024L => $"{SizeBytes / 1024d:0.0} KB",
        _ => $"{SizeBytes} B",
    };

    public string ExtensionText => Extension.TrimStart('.').ToUpperInvariant();

    public double ProgressPercent => ProgressTotal == 0
        ? 0
        : (double)ProgressCurrent / ProgressTotal * 100;

    public bool IsProcessing => State is JobState.Validating or JobState.Running or JobState.Exporting;

    public bool IsSucceeded => State == JobState.Succeeded;

    public bool HasError => State == JobState.Failed;

    public bool CanOpenResult => IsSucceeded && !string.IsNullOrWhiteSpace(OutputPath);

    public void BeginWorksheetDiscovery()
    {
        if (!RequiresWorksheetSelection)
        {
            return;
        }

        WorksheetOptions = [ExcelExportScopeOption.AllWorksheets];
        SelectedWorksheetOption = ExcelExportScopeOption.AllWorksheets;
        WorksheetSelectionStatusText = "正在读取工作表";
        HasWorksheetDiscoveryWarning = false;
        WorksheetDiscoveryState = WorksheetDiscoveryState.Loading;
        if (State == JobState.Queued)
        {
            StatusText = "正在读取工作表";
        }
    }

    public void BeginExcelToolPreflight()
    {
        if (!RequiresExcelToolPreflight)
        {
            return;
        }

        ExcelCompatibility = null;
        ExcelToolWorksheetNames = [];
        ExcelToolPreflightSummaryText = "正在检查";
        ExcelToolPreflightStatusText = "正在读取工作簿兼容性";
        ExcelToolPreflightState = ExcelToolPreflightState.Loading;
        if (State == JobState.Queued)
        {
            StatusText = "正在检查 Excel 兼容性";
        }
    }

    public void CompleteExcelToolPreflight(
        IReadOnlyList<ExcelWorksheetDescriptor> worksheets,
        ExcelWorkbookCompatibilityReport? compatibility)
    {
        ArgumentNullException.ThrowIfNull(worksheets);
        if (!RequiresExcelToolPreflight)
        {
            return;
        }

        ExcelToolWorksheetNames = worksheets.Select(static worksheet => worksheet.Name).ToArray();
        ExcelCompatibility = compatibility ?? new ExcelWorkbookCompatibilityReport(
            worksheets.Count(static worksheet => worksheet.Visibility != "unknown"),
            worksheets.Count(static worksheet =>
                worksheet.Visibility is "hidden" or "very-hidden"),
            worksheets.Count(static worksheet => worksheet.Visibility == "unknown"),
            0,
            false,
            false,
            []);
        var details = new List<string>
        {
            $"{ExcelCompatibility.WorksheetCount} 张工作表",
        };
        if (ExcelCompatibility.HiddenWorksheetCount > 0)
        {
            details.Add($"{ExcelCompatibility.HiddenWorksheetCount} 张隐藏表");
        }

        if (ExcelCompatibility.ChartSheetCount > 0)
        {
            details.Add($"{ExcelCompatibility.ChartSheetCount} 张图表工作表（合并/拆分不支持）");
        }

        if (ExcelCompatibility.ExternalLinkCount > 0)
        {
            details.Add($"{ExcelCompatibility.ExternalLinkCount} 个外部链接");
        }

        if (ExcelCompatibility.HasVbaProject)
        {
            details.Add("包含 VBA（当前操作不支持）");
        }

        if (ExcelCompatibility.CompressionRisks.Count > 0)
        {
            details.Add($"压缩风险：{string.Join("、", ExcelCompatibility.CompressionRisks)}");
        }

        if (ExcelCompatibility.Uses1904DateSystem)
        {
            details.Add("使用 1904 日期系统");
        }

        ExcelToolPreflightSummaryText = HasExcelToolPreflightWarning
            ? $"{ExcelCompatibility.WorksheetCount} 张表 · 有风险"
            : $"{ExcelCompatibility.WorksheetCount} 张表 · 可处理";
        ExcelToolPreflightStatusText = string.Join("；", details);
        ExcelToolPreflightState = ExcelToolPreflightState.Ready;
        if (State == JobState.Queued)
        {
            StatusText = HasExcelToolPreflightWarning ? "发现 Excel 兼容性风险" : "兼容性检查通过";
        }
    }

    public void FailExcelToolPreflight(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!RequiresExcelToolPreflight)
        {
            return;
        }

        ExcelCompatibility = null;
        ExcelToolWorksheetNames = [];
        ExcelToolPreflightSummaryText = "检查失败";
        ExcelToolPreflightStatusText = message;
        ExcelToolPreflightState = ExcelToolPreflightState.Failed;
        if (State == JobState.Queued)
        {
            StatusText = message;
        }
    }

    public void CompleteWorksheetDiscovery(IReadOnlyList<ExcelWorksheetDescriptor> worksheets)
    {
        ArgumentNullException.ThrowIfNull(worksheets);
        var options = new List<ExcelExportScopeOption>(worksheets.Count + 1)
        {
            ExcelExportScopeOption.AllWorksheets,
        };
        options.AddRange(worksheets.Select(static worksheet => new ExcelExportScopeOption(
            ExcelExportScopeKind.SingleWorksheet,
            worksheet.IsExportable
                ? worksheet.Name
                : $"{worksheet.Name}（{GetVisibilityLabel(worksheet.Visibility)}）",
            worksheet.Name,
            worksheet.IsExportable,
            worksheet.IsExportable
                ? $"仅导出工作表：{worksheet.Name}"
                : $"{worksheet.Name} 是{GetVisibilityLabel(worksheet.Visibility)}工作表，当前不可单独导出")));

        WorksheetOptions = options;
        SelectedWorksheetOption = ExcelExportScopeOption.AllWorksheets;
        WorksheetSelectionStatusText = $"已读取 {worksheets.Count} 张工作表";
        HasWorksheetDiscoveryWarning = false;
        WorksheetDiscoveryState = WorksheetDiscoveryState.Ready;
        if (State == JobState.Queued)
        {
            StatusText = "待处理";
        }
    }

    public void FailWorksheetDiscovery(string message)
    {
        if (!RequiresWorksheetSelection)
        {
            return;
        }

        WorksheetOptions = [ExcelExportScopeOption.AllWorksheets];
        SelectedWorksheetOption = ExcelExportScopeOption.AllWorksheets;
        WorksheetSelectionStatusText = message;
        HasWorksheetDiscoveryWarning = true;
        WorksheetDiscoveryState = WorksheetDiscoveryState.Failed;
        if (State == JobState.Queued)
        {
        StatusText = $"{message}；请重试或确认导出全部";
        }
    }

    public void ConfirmAllWorksheetsFallback()
    {
        if (!CanConfirmAllWorksheetsFallback)
        {
            return;
        }

        SelectedWorksheetOption = ExcelExportScopeOption.AllWorksheets;
        WorksheetSelectionStatusText = "读取失败，已确认导出全部工作表";
        WorksheetDiscoveryState = WorksheetDiscoveryState.Ready;
        if (State == JobState.Queued)
        {
            StatusText = "待处理（导出全部工作表）";
        }
    }

    public void BeginPdfPreflight()
    {
        if (!RequiresPdfPreflight)
        {
            return;
        }

        PdfPageCount = null;
        HasPdfSignature = false;
        IsPdfEncrypted = false;
        PdfPreflightStatusText = "正在检查 PDF";
        PdfPreflightState = PdfPreflightState.Loading;
        if (State == JobState.Queued)
        {
            StatusText = "正在检查 PDF";
        }
    }

    public void CompletePdfPreflight(int pageCount, bool hasSignature, bool isEncrypted = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageCount, 1);
        if (!RequiresPdfPreflight)
        {
            return;
        }

        PdfPageCount = pageCount;
        HasPdfSignature = hasSignature;
        IsPdfEncrypted = isEncrypted;
        PdfPreflightStatusText = hasSignature
            ? BlocksSignedPdfChanges
                ? $"{pageCount} 页；检测到数字签名"
                : $"{pageCount} 页；数字签名输入将只读保留"
            : isEncrypted
                ? $"{pageCount} 页；密码验证通过，将输出解锁副本"
                : $"{pageCount} 页；结构检查通过";
        PdfPreflightState = PdfPreflightState.Ready;
        if (State == JobState.Queued)
        {
            StatusText = hasSignature && BlocksSignedPdfChanges
                ? "包含数字签名，已阻止修改"
                : "待处理";
        }
    }

    public void FailPdfPreflight(string message, bool isEncrypted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (!RequiresPdfPreflight)
        {
            return;
        }

        PdfPageCount = null;
        HasPdfSignature = false;
        IsPdfEncrypted = isEncrypted;
        PdfPreflightStatusText = message;
        PdfPreflightState = PdfPreflightState.Failed;
        if (State == JobState.Queued)
        {
            StatusText = message;
        }
    }

    public void SetWorksheetSelectionLocked(bool isLocked)
    {
        if (_isWorksheetSelectionLocked == isLocked)
        {
            return;
        }

        _isWorksheetSelectionLocked = isLocked;
        OnPropertyChanged(nameof(IsWorksheetSelectionEnabled));
        OnPropertyChanged(nameof(CanRetryWorksheetDiscovery));
        OnPropertyChanged(nameof(CanConfirmAllWorksheetsFallback));
    }

    public void MarkValidating()
    {
        TransitionTo(JobState.Validating);
        StatusText = "正在预检";
        ErrorCode = null;
        OutputPath = null;
        ProgressCurrent = 0;
        ProgressTotal = 3;
    }

    public void MarkRunning(string? statusText = null)
    {
        TransitionTo(JobState.Running);
        StatusText = string.IsNullOrWhiteSpace(statusText) ? "正在启动 Office" : statusText;
    }

    public void ReportProgress(WorkerProgressMessage progress)
    {
        ProgressCurrent = progress.Current;
        ProgressTotal = progress.Total;
        StatusText = progress.Stage switch
        {
            "preflight" => "预检完成",
            "office-export" => "正在调用 Office",
            "word-created" or "excel-created" => "Office 已启动",
            "word-configured" or "excel-configured" => "正在应用安全设置",
            "excel-identity" => "已隔离 Excel 进程",
            "document-opened" or "workbook-opened" => "正在导出 PDF",
            "pdf-exported" => "正在校验 PDF",
            "pdf-opened" => "正在读取 PDF",
            "page-extracting" => "正在分析文字层与表格结构",
            "ocr-rendering" => "正在渲染扫描页",
            "ocr-recognizing" => "正在执行本地 OCR",
            "workbook-writing" => "正在生成 Excel",
            "workbook-validating" => "正在校验 Excel",
            "completed" => "正在完成",
            _ => "正在处理",
        };
    }

    public void MarkSucceeded(string outputPath, string? statusText = null)
    {
        TransitionTo(JobState.Succeeded);
        OutputPath = outputPath;
        StatusText = string.IsNullOrWhiteSpace(statusText) ? "处理完成" : statusText;
        ProgressCurrent = Math.Max(ProgressTotal, 1);
        ProgressTotal = Math.Max(ProgressTotal, 1);
    }

    public void MarkFailed(string errorCode, string message)
    {
        TransitionTo(JobState.Failed);
        ErrorCode = errorCode;
        StatusText = message;
    }

    public void MarkCanceled()
    {
        TransitionTo(JobState.Canceled);
        StatusText = "已取消";
    }

    public void ApplyRenamePlanItem(RenamePlanItem item, string statusText)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(statusText);
        if (!RequiresRenamePreview ||
            !string.Equals(item.Source.FullPath, FullPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        RenameCalculatedName = item.CalculatedFileName;
        SetProperty(ref _renamePreviewName, item.ProposedFileName, nameof(RenamePreviewName));
        RenamePlanStatus = item.Status;
        RenameValidationCode = item.ErrorCode;
        if (State == JobState.Queued)
        {
            StatusText = statusText;
        }
    }

    public void ClearRenameManualOverride()
    {
        if (!RequiresRenamePreview)
        {
            return;
        }

        RenameManualOverride = null;
    }

    public void MarkRenameUndoSucceeded()
    {
        if (!RequiresRenamePreview || State != JobState.Succeeded)
        {
            return;
        }

        OutputPath = FullPath;
        StatusText = "已撤销重命名";
    }

    private void TransitionTo(JobState next)
    {
        JobStateMachine.EnsureTransition(State, next);
        State = next;
    }

    private static string GetVisibilityLabel(string visibility) => visibility switch
    {
        "hidden" => "隐藏",
        "very-hidden" => "深度隐藏",
        "visible" => "可见",
        _ => "不支持",
    };
}
