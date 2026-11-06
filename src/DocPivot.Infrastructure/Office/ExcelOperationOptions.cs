using DocPivot.Core.Excel;

namespace DocPivot.Infrastructure.Office;

public sealed record ExcelOperationOptions(
    bool IncludeHiddenWorksheets,
    bool PreserveExternalLinks,
    bool SkipUnsafeCompressionSheets,
    bool ConvertLegacyWorkbookToOpenXml,
    ExcelImageCompressionLevel ImageCompressionLevel,
    IReadOnlyList<string>? SelectedWorksheets = null,
    ExcelSplitOutputMode SplitOutputMode = ExcelSplitOutputMode.SeparateFiles,
    int? CompressionStrength = null)
{
    public static ExcelOperationOptions SafeDefaults { get; } = new(
        IncludeHiddenWorksheets: true,
        PreserveExternalLinks: true,
        SkipUnsafeCompressionSheets: true,
        ConvertLegacyWorkbookToOpenXml: true,
        ImageCompressionLevel: ExcelImageCompressionLevel.None);
}
