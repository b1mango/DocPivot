using System.ComponentModel;
using System.Globalization;
using System.IO;
using DocPivot.Core.Documents;

namespace DocPivot.App.ViewModels;

/// <summary>
/// Derived, display-only state for the conversational workspace: the question on the
/// empty page, the heading over the list, the breadcrumb and the composer's button.
/// Kept apart from the processing logic so the view stays free of converters.
/// </summary>
public partial class WorkspaceViewModel
{
    private static readonly string[] PresentationDependencies =
    [
        nameof(PdfVisualSplitSummary),
        nameof(QueueCountText),
        nameof(HasFiles),
        nameof(SelectedTool),
        nameof(SelectedPdfToolMode),
        nameof(SelectedPdfSplitMode),
        nameof(SelectedExcelToolMode),
        nameof(SelectedRenameToolMode),
        nameof(ShowPdfVisualPreview),
        nameof(IsMergeOrderingEnabled),
        nameof(IsProcessing),
        nameof(RenamePreviewSummary),
        nameof(PrimaryActionText),
        nameof(OutputDirectory),
        nameof(HasCompletedResult),
    ];

    private static readonly string[] PresentationProperties =
    [
        nameof(HeaderSummary),
        nameof(ShowPageThumbnails),
        nameof(CanReorderFiles),
        nameof(AddRowText),
        nameof(EmptyStateQuestion),
        nameof(ComposerPlaceholder),
        nameof(WorkspaceHeading),
        nameof(WorkspaceSubheading),
        nameof(ModeTitle),
        nameof(HasModeTitle),
        nameof(HasPdfModes),
        nameof(HasExcelModes),
        nameof(GoText),
        nameof(OutputFolderName),
        nameof(IsEmptyState),
        nameof(ShowComposerOptions),
        nameof(ShowFileList),
        nameof(ShowMergeBoard),
        nameof(ShowSheetBoard),
        nameof(ShowAddRow),
    ];

    /// <summary>One quiet line under the tool title, e.g. "7 个文件 · 13 页".</summary>
    public string HeaderSummary
    {
        get
        {
            if (IsPdfMergeMode && HasFiles)
            {
                return PdfVisualSplitSummary;
            }

            if (ShowPageThumbnails)
            {
                return PdfVisualSplitSummary;
            }

            if (ShowSheetBoard)
            {
                var sheets = Files.Sum(static file => file.SplitSheets.Count);
                var selected = Files.Sum(static file => file.SelectedSplitSheetCount);
                return sheets == 0
                    ? $"{Files.Count} 个工作簿"
                    : $"{Files.Count} 个工作簿 · 已选 {selected} / {sheets} 张工作表";
            }

            return HasFiles ? $"{Files.Count} 个文件" : string.Empty;
        }
    }

    public bool ShowPageThumbnails => IsPdfVisualSplitMode && PdfPageThumbnails.Count > 0;

    /// <summary>
    /// Queued PDFs to merge are laid out as pages to arrange; once the run starts the familiar
    /// list takes over, because that is where per-file progress and results are shown.
    /// </summary>
    public bool ShowMergeBoard => IsPdfMergeMode && HasFiles && !IsProcessing && !HasCompletedResult;

    public bool ShowFileList => HasFiles && !ShowPageThumbnails && !ShowMergeBoard && !ShowSheetBoard;

    /// <summary>"继续添加" and "清空" belong to an editable list or board, not to a running or finished batch.</summary>
    public bool ShowAddRow =>
        (ShowFileList || ShowMergeBoard || ShowSheetBoard) && !IsProcessing && !HasCompletedResult;

    public bool CanReorderFiles => IsMergeOrderingEnabled && !IsProcessing;

    public string AddRowText => IsBatchRenameTool ? "继续添加文件或文件夹" : "继续添加";

    /// <summary>The empty page is a single question and a composer, nothing else.</summary>
    public bool IsEmptyState => !HasFiles && !HasCompletedResult;

    /// <summary>The composer's tool options show only while there is something to act on.</summary>
    public bool ShowComposerOptions => HasFiles && !IsProcessing && !HasCompletedResult;

    public string EmptyStateQuestion => SelectedTool.Operation switch
    {
        DocumentOperation.OfficeToPdf => "要把哪些文档转成 PDF？",
        DocumentOperation.PdfToExcel => "要从哪些 PDF 里提取表格？",
        DocumentOperation.ExcelOperations => "要处理哪些工作簿？",
        DocumentOperation.PdfOperations => "要处理哪些 PDF？",
        DocumentOperation.BatchRename => "要给哪些文件改名？",
        _ => "要处理哪些文件？",
    };

    public string ComposerPlaceholder => SelectedTool.Operation switch
    {
        DocumentOperation.OfficeToPdf => "把文档拖到这里，或者点击选择",
        DocumentOperation.PdfToExcel or DocumentOperation.PdfOperations => "把 PDF 拖到这里，或者点击选择",
        DocumentOperation.ExcelOperations => "把工作簿拖到这里，或者点击选择",
        _ => "把文件或文件夹拖到这里，或者点击选择",
    };

    public string WorkspaceHeading
    {
        get
        {
            var count = Files.Count;
            return SelectedTool.Operation switch
            {
                DocumentOperation.OfficeToPdf => $"转换 {count} 个文件",
                DocumentOperation.PdfToExcel => $"提取 {count} 个 PDF",
                DocumentOperation.ExcelOperations => $"{SelectedExcelToolMode.Title} {count} 个工作簿",
                DocumentOperation.PdfOperations when IsPdfSplitMode && count == 1 => Files[0].FileName,
                DocumentOperation.PdfOperations => $"{SelectedPdfToolMode.Title} {count} 个 PDF",
                DocumentOperation.BatchRename => $"重命名 {count} 个文件",
                _ => $"{count} 个文件",
            };
        }
    }

    public string WorkspaceSubheading
    {
        get
        {
            if (!HasFiles)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            if (IsBatchRenameTool)
            {
                parts.Add(RenameChangedCount == 0 ? "还没有名称会改变" : $"{RenameChangedCount} 个会改变");
                if (RenameConflictCount > 0)
                {
                    parts.Add($"{RenameConflictCount} 个冲突");
                }

                if (RenameInvalidCount > 0)
                {
                    parts.Add($"{RenameInvalidCount} 个无效");
                }

                return string.Join(" · ", parts);
            }

            if (IsPdfSplitMode)
            {
                if (IsPdfVisualSplitMode && PdfPreviewTotalPages > 0)
                {
                    var outputs = _pdfCutPages.Count + 1;
                    return outputs == 1
                        ? $"{PdfPreviewTotalPages} 页 · 点页面之间的缝隙切一刀"
                        : $"{PdfPreviewTotalPages} 页 · 拆成 {outputs} 份";
                }

                var pages = Files[0].PdfPageCountText;
                return string.IsNullOrEmpty(pages)
                    ? $"按{SelectedPdfSplitMode.Title}拆分"
                    : $"{pages} · 按{SelectedPdfSplitMode.Title}拆分";
            }

            if (IsAnyPdfTool)
            {
                var pageCount = Files.Sum(static file => file.PdfPageCount ?? 0);
                if (pageCount > 0)
                {
                    parts.Add($"共 {pageCount} 页");
                }
            }

            parts.Add(FormatBytes(Files.Sum(static file => file.SizeBytes)));

            var checking = Files.Count(static file => file.IsPdfPreflightLoading || file.IsExcelToolPreflightLoading);
            if (checking > 0)
            {
                parts.Add($"{checking} 个正在检查");
            }

            if (IsMergeOrderingEnabled && Files.Count > 1 && !IsProcessing)
            {
                parts.Add("拖动调整顺序");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Second half of the breadcrumb ("PDF 工具 › 合并"); empty for single-purpose tools.</summary>
    public string ModeTitle => SelectedTool.Operation switch
    {
        DocumentOperation.PdfOperations => SelectedPdfToolMode.Title,
        DocumentOperation.ExcelOperations => SelectedExcelToolMode.Title,
        DocumentOperation.BatchRename => SelectedRenameToolMode.Title,
        _ => string.Empty,
    };

    public bool HasModeTitle => ModeTitle.Length > 0;

    public bool HasPdfModes => IsPdfOperationsTool;

    public bool HasExcelModes => IsExcelOperationsTool;

    /// <summary>The short verb on the composer's send button.</summary>
    public string GoText => SelectedTool.Operation switch
    {
        DocumentOperation.OfficeToPdf => "转换",
        DocumentOperation.PdfToExcel => "提取",
        DocumentOperation.ExcelOperations => SelectedExcelToolMode.Title,
        DocumentOperation.PdfOperations => SelectedPdfToolMode.Title,
        DocumentOperation.BatchRename => RenameChangedCount == 0 ? "重命名" : $"重命名 {RenameChangedCount} 个",
        _ => "开始",
    };

    /// <summary>"桌面", "文档" or the last folder name, never a full path.</summary>
    public string OutputFolderName => DescribeFolder(OutputDirectory);

    internal static string DescribeFolder(string? directory)
    {
        var trimmed = directory?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return "选择位置";
        }

        if (IsSameFolder(trimmed, Environment.SpecialFolder.DesktopDirectory))
        {
            return "桌面";
        }

        if (IsSameFolder(trimmed, Environment.SpecialFolder.MyDocuments))
        {
            return "文档";
        }

        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    private static bool IsSameFolder(string path, Environment.SpecialFolder folder)
    {
        var special = Environment.GetFolderPath(folder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return special.Length > 0 && string.Equals(path, special, StringComparison.OrdinalIgnoreCase);
    }

    internal static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024d:0} KB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024d / 1024d:0.#} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024d / 1024d / 1024d:0.##} GB"),
    };

    /// <summary>
    /// Status lines the interface already shows in place (the list, the undo pill, the
    /// result card) are not repeated as a toast.
    /// </summary>
    public static bool IsQuietStatus(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return true;
        }

        return message is "就绪" or "队列已清空" or "已更新输出目录" or "已从队列移除文件"
            || message.StartsWith("已移除", StringComparison.Ordinal)
            || message.StartsWith("已恢复", StringComparison.Ordinal)
            || message.StartsWith("已添加", StringComparison.Ordinal) && !message.Contains('，') && !message.Contains("跳过", StringComparison.Ordinal)
            || message.StartsWith("处理完成", StringComparison.Ordinal) && !message.Contains('；')
            || message.StartsWith("正在", StringComparison.Ordinal);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        OnEstimateDependencyChanged(e);
        if (e.PropertyName is null || Array.IndexOf(PresentationDependencies, e.PropertyName) < 0)
        {
            return;
        }

        foreach (var name in PresentationProperties)
        {
            base.OnPropertyChanged(new PropertyChangedEventArgs(name));
        }
    }
}
