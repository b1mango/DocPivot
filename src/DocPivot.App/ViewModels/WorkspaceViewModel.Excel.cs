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
    private readonly SemaphoreSlim _worksheetDiscoveryGate = new(1, 1);
    private readonly object _worksheetDiscoverySync = new();
    private readonly Dictionary<QueuedFileViewModel, CancellationTokenSource> _worksheetDiscoveryCancellations = [];
    private readonly Dictionary<QueuedFileViewModel, Task> _worksheetDiscoveryTasks = [];

    [ObservableProperty]
    private bool _includeHiddenWorksheets = true;

    [ObservableProperty]
    private bool _preserveExternalLinks = true;

    [ObservableProperty]
    private bool _skipUnsafeCompressionSheets = true;

    [ObservableProperty]
    private bool _convertLegacyWorkbookToOpenXml = true;

    public string ExcelMergePreviewSummary => GetExcelMergePreview().Summary;

    public string ExcelMergePreviewDetail => GetExcelMergePreview().Detail;

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
}
