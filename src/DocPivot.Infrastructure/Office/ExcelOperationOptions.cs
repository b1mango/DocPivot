using DocPivot.Core.Excel;

namespace DocPivot.Infrastructure.Office;

public sealed record ExcelOperationOptions(
    bool IncludeHiddenWorksheets,
    bool PreserveExternalLinks,
    bool SkipUnsafeCompressionSheets,
    bool ConvertLegacyWorkbookToOpenXml,
    ExcelImageCompressionLevel ImageCompressionLevel)
{
    public static ExcelOperationOptions SafeDefaults { get; } = new(
        IncludeHiddenWorksheets: true,
        PreserveExternalLinks: true,
        SkipUnsafeCompressionSheets: true,
        ConvertLegacyWorkbookToOpenXml: true,
        ImageCompressionLevel: ExcelImageCompressionLevel.None);
}
