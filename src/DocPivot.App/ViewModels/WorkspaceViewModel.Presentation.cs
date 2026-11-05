using System.ComponentModel;

namespace DocPivot.App.ViewModels;

/// <summary>
/// Derived, display-only state for the workspace header and list. Kept apart
/// from the processing logic so the view stays free of converters.
/// </summary>
public partial class WorkspaceViewModel
{
    private static readonly string[] HeaderDependencies =
    [
        nameof(PdfVisualSplitSummary),
        nameof(QueueCountText),
        nameof(HasFiles),
        nameof(SelectedTool),
        nameof(SelectedPdfToolMode),
        nameof(SelectedPdfSplitMode),
        nameof(SelectedExcelToolMode),
        nameof(ShowPdfVisualPreview),
        nameof(IsMergeOrderingEnabled),
        nameof(IsProcessing),
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

            return HasFiles ? $"{Files.Count} 个文件" : string.Empty;
        }
    }

    public bool ShowPageThumbnails => IsPdfVisualSplitMode && PdfPageThumbnails.Count > 0;

    public bool CanReorderFiles => IsMergeOrderingEnabled && !IsProcessing;

    public string AddRowText =>
        IsBatchRenameTool ? "添加文件夹，或拖到这里" :
        IsPdfOperationsTool ? "添加 PDF，或拖到这里" :
        "添加文件，或拖到这里";

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is null || Array.IndexOf(HeaderDependencies, e.PropertyName) < 0)
        {
            return;
        }

        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(HeaderSummary)));
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ShowPageThumbnails)));
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(CanReorderFiles)));
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(AddRowText)));
    }
}
