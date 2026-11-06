using DocPivot.Core.Excel;

namespace DocPivot.App.ViewModels;

public sealed record ExcelSplitOutputModeOption(
    ExcelSplitOutputMode Mode,
    string Title,
    string Description);
