using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocPivot.Core.Excel;
using DocPivot.Core.Jobs;
using DocPivot.Infrastructure.Office;

namespace DocPivot.App.ViewModels;

/// <summary>
/// The Excel split board: every queued workbook shows its worksheets as first-page previews; the user
/// ticks the ones to keep and exports them one workbook per sheet, or together in a single workbook.
/// </summary>
public partial class WorkspaceViewModel
{
    private const int SheetPreviewPixelWidth = 360;

    private readonly IExcelWorksheetPreviewService? _excelWorksheetPreviewService;
    private readonly SemaphoreSlim _sheetPreviewGate = new(1, 1);

    [ObservableProperty]
    private ExcelSplitOutputModeOption _selectedExcelSplitOutputMode;

    public IReadOnlyList<ExcelSplitOutputModeOption> ExcelSplitOutputModes { get; } =
    [
        new(ExcelSplitOutputMode.SeparateFiles, "单独导出", "每张选中的工作表各存为一个工作簿"),
        new(ExcelSplitOutputMode.SingleWorkbook, "合并导出", "选中的工作表按原顺序放进一个工作簿"),
    ];

    public bool IsExcelSplitMode =>
        IsExcelOperationsTool && SelectedExcelToolMode.Mode == ExcelToolMode.Split;

    /// <summary>
    /// Like the merge board, the sheet board is for choosing; once the run starts the file list
    /// takes over because that is where progress and results are shown.
    /// </summary>
    public bool ShowSheetBoard => IsExcelSplitMode && HasFiles && !IsProcessing && !HasCompletedResult;

    [RelayCommand]
    private void ToggleAllSplitSheets(QueuedFileViewModel? file)
    {
        if (file is not null && !IsProcessing)
        {
            file.SetAllSplitSheetsSelected(!file.AreAllSplitSheetsSelected);
        }
    }

    partial void OnSelectedExcelSplitOutputModeChanged(ExcelSplitOutputModeOption value)
    {
        ClearCompletedResult();
        OnPropertyChanged(nameof(PrimaryActionText));
    }

    private ExcelOperationOptions WithSplitSelection(ExcelOperationOptions options, QueuedFileViewModel file) =>
        options with
        {
            // The board is the authority on which sheets leave the workbook, hidden ones included.
            IncludeHiddenWorksheets = true,
            SelectedWorksheets = file.SelectedSplitSheetNames,
            SplitOutputMode = SelectedExcelSplitOutputMode.Mode,
        };

    private static bool HasSplitSelection(QueuedFileViewModel file) =>
        file.SplitSheets.Count == 0 || file.SelectedSplitSheetCount > 0;

    private void RequestSheetPreviews()
    {
        if (!IsExcelSplitMode || _excelWorksheetPreviewService is null || _isDisposed)
        {
            return;
        }

        foreach (var file in Files)
        {
            if (file.SplitSheets.Count > 0 && !file.SheetPreviewsRequested && file.State == JobState.Queued)
            {
                file.SheetPreviewsRequested = true;
                _ = LoadSheetPreviewsAsync(_excelWorksheetPreviewService, file, file.SplitSheets);
            }
        }
    }

    private async Task LoadSheetPreviewsAsync(
        IExcelWorksheetPreviewService service,
        QueuedFileViewModel file,
        IReadOnlyList<ExcelSheetCardViewModel> sheets)
    {
        var token = _lifetimeCancellation.Token;
        try
        {
            // Each preview run drives Excel; one workbook at a time keeps the machine responsive.
            await _sheetPreviewGate.WaitAsync(token);
            IReadOnlyList<ExcelWorksheetPreview> previews;
            try
            {
                previews = await service.RenderPreviewsAsync(
                    file.FullPath,
                    Path.Combine(Path.GetTempPath(), "DocPivot", "sheet-previews"),
                    SheetPreviewPixelWidth,
                    token);
            }
            finally
            {
                _sheetPreviewGate.Release();
            }

            foreach (var sheet in sheets)
            {
                var preview = previews.FirstOrDefault(item => string.Equals(item.Name, sheet.Name, StringComparison.Ordinal));
                sheet.ShowPreview(preview?.ImagePath, preview?.PageCount);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The window is closing.
        }
        catch (Exception)
        {
            // Previews only help choosing; the sheet names alone still let the user split.
            foreach (var sheet in sheets)
            {
                sheet.ShowPreviewUnavailable();
            }
        }
    }
}
