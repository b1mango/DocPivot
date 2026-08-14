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

public partial class WorkspaceViewModel
{
    private readonly SemaphoreSlim _pdfPreflightGate = new(2, 2);
    private readonly object _pdfPreflightSync = new();
    private readonly Dictionary<QueuedFileViewModel, CancellationTokenSource> _pdfPreflightCancellations = [];
    private readonly Dictionary<QueuedFileViewModel, Task> _pdfPreflightTasks = [];

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
    private string _pdfMergeOutputName = "合并 PDF";

    public PdfCompressionProfile CurrentPdfCompressionProfile =>
        PdfCompressionProfile.FromStrength(PdfCompressionStrength);

    public string PdfCompressionDetail => CurrentPdfCompressionProfile.IsLossless
        ? "无损优化"
        : $"{CurrentPdfCompressionProfile.ImageDpi} DPI · JPEG {CurrentPdfCompressionProfile.JpegQuality}%";

    public string PdfCompressionPreservationNote => CurrentPdfCompressionProfile.IsLossless
        ? "不重采样图片；数字签名文件将对副本强制压缩，源文件保持原样。"
        : "线稿自动保留无损编码，扫描文字不低于 150 DPI；数字签名文件将对副本强制压缩，源文件保持原样。输出校验页数与可搜索文字层，批注、表单和附件不保证保留。";

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
        var outputBaseName = string.IsNullOrWhiteSpace(PdfMergeOutputName)
            ? "merged"
            : Path.GetFileNameWithoutExtension(PdfMergeOutputName.Trim());
        outputBaseName = WindowsOutputNamePolicy.SanitizeStem(outputBaseName);
        var outputPath = CreateUniqueNamedOutputPath(outputDirectory, outputBaseName, ".pdf");
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
                    null,
                    files.Select(static file => file.PdfRotation).ToArray()),
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
}
