using DocPivot.Core.Excel;

namespace DocPivot.Core.Contracts;

public sealed record WorkerExcelToolOptions(
    bool IncludeHiddenWorksheets,
    bool PreserveExternalLinks,
    bool SkipUnsafeCompressionSheets,
    bool ConvertLegacyWorkbookToOpenXml,
    ExcelImageCompressionLevel ImageCompressionLevel)
{
    public static WorkerExcelToolOptions SafeDefaults { get; } = new(
        IncludeHiddenWorksheets: true,
        PreserveExternalLinks: true,
        SkipUnsafeCompressionSheets: true,
        ConvertLegacyWorkbookToOpenXml: true,
        ImageCompressionLevel: ExcelImageCompressionLevel.None);
}
