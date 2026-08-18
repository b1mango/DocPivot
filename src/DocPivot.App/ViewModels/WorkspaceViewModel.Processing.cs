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
    private CancellationTokenSource? _processingCancellation;
    private OfficeEngineStatus? _officeEngineStatus;
    private PdfEngineStatus? _pdfEngineStatus;
    private PdfTableEngineStatus? _pdfTableEngineStatus;
    private string? _pdfEngineProbeError;

    [ObservableProperty]
    private string _engineStatusTitle = "正在检测 Office";

    [ObservableProperty]
    private string _engineStatusDetail = "正在确认本机 Word 和 Excel 可用性";

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
                        PdfCombineIntoOneWorksheet
                            ? WorkerPdfWorksheetMode.OneWorksheetPerDocument
                            : WorkerPdfWorksheetMode.OneWorksheetPerTable,
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

    private sealed record PendingOfficeConversion(
        QueuedFileViewModel File,
        OfficeConversionOptions? Options);
}
