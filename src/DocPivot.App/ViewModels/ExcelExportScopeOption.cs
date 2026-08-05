namespace DocPivot.App.ViewModels;

public sealed record ExcelExportScopeOption(
    ExcelExportScopeKind Kind,
    string DisplayName,
    string? WorksheetName,
    bool IsEnabled,
    string ToolTip)
{
    public static ExcelExportScopeOption AllWorksheets { get; } = new(
        ExcelExportScopeKind.AllWorksheets,
        "全部工作表",
        null,
        true,
        "导出当前工作簿中的全部工作表");
}
